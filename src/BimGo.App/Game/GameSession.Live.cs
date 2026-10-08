using System.Numerics;
using BimGo.Live;
using BimGo.Physics;
using BimGo.Scene;
using Win32 = BimGo.Native.Win32;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Live Revit sessions: messages from Revit, refresh (F5 / Go pressed again), "show in Revit", and the
    /// end-of-session hooks the app uses to switch models or reload a snapshot where the player stands.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private readonly ILiveLink _live;
        private bool _reloadAnnounced;

        #endregion

        #region State

        /// <summary>The live session, or null for a .bimgo file.</summary>
        public ILiveLink LiveLink => _live;

        /// <summary>True when connected to a live Revit session.</summary>
        public bool IsLiveConnected => _live != null && _live.Connected;

        #endregion

        #region Host requests

        /// <summary>
        /// Ends the session for a reason (the app switches model or reloads). Unsaved changes are still confirmed;
        /// cancelling keeps the session running.
        /// </summary>
        public void RequestEnd(SessionEndReason reason)
        {
            _endReason = reason;
            _endRequested = true;
        }

        /// <summary>
        /// Where the player stands (Revit internal metres), for carrying across a reload.
        /// </summary>
        public SessionPose CapturePose()
        {
            if (_player == null) { return null; }
            Vector3 origin = Scene.OriginOffset;
            return new SessionPose
            {
                Feet = _player.Feet + origin,
                Yaw = _player.Yaw,
                Pitch = _player.Pitch,
                Flying = _player.Flying,
                HomeFeet = _player.HomeFeet + origin,
                HomeYaw = _player.HomeYaw,
                HomePitch = _player.HomePitch,
                HomeFlying = _player.HomeFlying,
                ActiveGun = _activeGun,
                ShowMap = _showMap
            };
        }

        /// <summary>
        /// Puts the player back where a pose says (the new snapshot's origin may differ).
        /// </summary>
        private void ApplyPose(SessionPose pose)
        {
            Vector3 origin = Scene.OriginOffset;
            if (pose.Flying != _player.Flying) { _player.ToggleFly(); }
            _player.TeleportTo(pose.Feet - origin, pose.Yaw, pose.Pitch);
            _player.SetHome(pose.HomeFeet - origin, pose.HomeYaw, pose.HomePitch, pose.HomeFlying);
            if (pose.ActiveGun >= 0 && pose.ActiveGun < _guns.Length) { _activeGun = pose.ActiveGun; }
            _showMap = pose.ShowMap;
        }

        #endregion

        #region Live session

        /// <summary>
        /// Per frame: shows Revit's notices and reloads when a newer snapshot is ready (once nothing is in flight).
        /// </summary>
        private void UpdateLive()
        {
            if (_live == null) { return; }

            while (_live.TryTakeNotice(out string notice))
            {
                if (!string.IsNullOrEmpty(notice)) { Toast(notice, 4f, important: true); }
            }

            if (_live.SnapshotReady && !_endRequested && !_reloadAnnounced)
            {
                bool busy = EditsPending > 0 || _guns[_activeGun].CapturesInput || IsEditingComment;
                if (busy) { return; }

                _reloadAnnounced = true;
                RequestEnd(SessionEndReason.Reload);
            }
        }

        /// <summary>
        /// F5: asks Revit for a fresh snapshot (the walkthrough reloads where the player stands when it arrives).
        /// </summary>
        private void RefreshFromRevit()
        {
            if (_live == null)
            {
                Toast("Refresh needs a live Revit session (press Go in Revit)");
                return;
            }
            if (!_live.Connected)
            {
                Toast("Not connected to Revit", important: true);
                return;
            }
            if (EditsPending > 0)
            {
                Toast("Waiting for Revit to finish your edits first");
                return;
            }
            if (_live.Refreshing)
            {
                Toast("Revit is already extracting a fresh snapshot…");
                return;
            }

            if (_live.RequestRefresh())
            {
                Sound.Play(Audio.SoundId.UiClick);
                Toast("Asking Revit for a fresh snapshot…", 4f);
            }
        }

        /// <summary>
        /// Selects and shows an element in Revit (Scan gun, R).
        /// </summary>
        /// <param name="element">The source element index.</param>
        /// <param name="dynamicId">The dynamic instance id, or 0 for a static element.</param>
        public void ShowInRevit(int element, int dynamicId)
        {
            if (element < 0) { return; }
            if (_live == null)
            {
                Toast("Show in Revit needs a live Revit session (press Go in Revit)");
                return;
            }
            if (!_live.Connected)
            {
                Toast("Not connected to Revit", important: true);
                return;
            }

            // A linked element can't be selected on its own: show the link instance it belongs to
            long revitId = Scene.Elements[element].ElementId;
            LinkInfo link = Scene.LinkOf(Scene.Elements[element]);
            if (link != null)
            {
                if (link.InstanceId <= 0)
                {
                    Toast("That element is in a linked model");
                    return;
                }
                revitId = link.InstanceId;
            }
            if (dynamicId > 0)
            {
                DynamicInstance instance = Dynamics.Find(dynamicId);
                revitId = instance?.RevitId ?? 0;
            }
            if (revitId <= 0)
            {
                Toast("That clone hasn't been created in Revit yet");
                return;
            }

            // Let Revit take the foreground when it shows the element
            Win32.AllowSetForegroundWindow(_live.RevitPid);
            bool sent = link != null
                ? _live.ShowLinkedElement(link.InstanceId, Scene.Elements[element].ElementId)
                : _live.ShowElements(new[] { revitId });
            if (sent)
            {
                Sound.Play(Audio.SoundId.UiClick);
                Toast(link != null ? $"Showing it in the link “{link.Label}” in Revit…" : "Showing in Revit…");
            }
        }

        #endregion
    }
}
