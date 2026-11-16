using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace SupinePosition
{
    // Runs after everything else so we override the animator + camera for the frame.
    [DefaultExecutionOrder(32000)]
    [BepInPlugin("com.supine.position", "Supine Position", "1.4.0")]
    public class SupinePlugin : BaseUnityPlugin
    {
        private ConfigEntry<KeyCode> _backKey, _forwardKey, _toggleKey;
        private ConfigEntry<float> _enterDelay, _pivotHeight, _cameraHeight, _armForwardShift, _armHeightShift, _cameraForward, _transition;
        private ConfigEntry<bool> _autoEnter, _forceBodyVisible, _freezeHands;

        private Player _player;
        private Transform _pelvis, _head;
        private readonly List<Transform> _collars = new List<Transform>();
        // Arms + weapon roots: these keep their ORIGINAL (un-flipped) world pose, sorted parent-first.
        private readonly List<Transform> _keep = new List<Transform>();
        private readonly List<Vector3> _keepPos = new List<Vector3>();
        private readonly List<Quaternion> _keepRot = new List<Quaternion>();
        private readonly List<SkinnedMeshRenderer> _renderers = new List<SkinnedMeshRenderer>();
        private readonly Dictionary<SkinnedMeshRenderer, ShadowCastingMode> _origShadow =
            new Dictionary<SkinnedMeshRenderer, ShadowCastingMode>();

        // Camera-relative lock for arms + weapon (re-applied right before rendering).
        private readonly List<Vector3> _relPos = new List<Vector3>();
        private readonly List<Quaternion> _relRot = new List<Quaternion>();
        private bool _lockActive, _loggedHands;
        private Vector3 _lastBase, _lastSet;
        private bool _haveLast;

        // First-person weapon renderers (under "Weapon_root"): forced visible while supine.
        private readonly List<Renderer> _wRend = new List<Renderer>();
        private readonly Dictionary<Renderer, ShadowCastingMode> _wOrig = new Dictionary<Renderer, ShadowCastingMode>();
        private bool _loggedWeapon;

        // Hands/arms rigidly attached to the weapon (pose stored relative to the weapon).
        private readonly List<Transform> _arm = new List<Transform>();
        private readonly List<Vector3> _armRelPos = new List<Vector3>();
        private readonly List<Quaternion> _armRelRot = new List<Quaternion>();
        private bool _armCaptured;
        private Transform _weaponT; // the held weapon's GameObject (NOT under the player skeleton)

        private bool _supine, _wasSupine, _loggedRenderers, _loggedCam;
        private float _backHeld;
        private float _t; // 0 = normal prone, 1 = fully supine (animated)

        private void OnEnable() { Camera.onPreCull += OnCamPreCull; }
        private void OnDisable() { Camera.onPreCull -= OnCamPreCull; _lockActive = false; }

        // Right before a camera culls/renders, snap arms + weapon back to their camera-relative pose,
        // so nothing the game does later in the frame can move the weapon away from the view.
        private void OnCamPreCull(Camera c)
        {
            if (!_lockActive || _keep.Count == 0 || _relPos.Count != _keep.Count) return;
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 cp = cam.transform.position; Quaternion cr = cam.transform.rotation;
            for (int i = 0; i < _keep.Count; i++)
            {
                var t = _keep[i];
                if (t == null) continue;
                t.rotation = cr * _relRot[i];
                t.position = cp + cr * _relPos[i];
            }
            ApplyArmLock();
        }

        // Put every arm/hand bone at its stored pose relative to the weapon, so the hands can
        // never move on their own while the weapon is held.
        private void ApplyArmLock()
        {
            if (!_armCaptured || _weaponT == null || _arm.Count == 0 || _armRelPos.Count != _arm.Count) return;
            Vector3 wp = _weaponT.position; Quaternion wr = _weaponT.rotation;
            for (int i = 0; i < _arm.Count; i++)
            {
                var a = _arm[i];
                if (a == null) continue;
                a.rotation = wr * _armRelRot[i];
                a.position = wp + wr * _armRelPos[i];
            }
        }

        private void CaptureArms()
        {
            if (_weaponT == null || _arm.Count == 0) return;
            if (_freezeHands.Value && _armCaptured) return;
            Vector3 wp = _weaponT.position; Quaternion inv = Quaternion.Inverse(_weaponT.rotation);
            _armRelPos.Clear(); _armRelRot.Clear();
            for (int i = 0; i < _arm.Count; i++)
            {
                var a = _arm[i];
                _armRelPos.Add(a != null ? inv * (a.position - wp) : Vector3.zero);
                _armRelRot.Add(a != null ? inv * a.rotation : Quaternion.identity);
            }
            _armCaptured = true;
        }

        private void Awake()
        {
            _autoEnter  = Config.Bind("General", "AutoEnterOnBackward", true,
                "Hold the backward key while prone to roll onto your back.");
            _toggleKey  = Config.Bind("General", "ToggleKey", KeyCode.Y,
                "Manual toggle while prone (works even if AutoEnter is off).");
            _forceBodyVisible = Config.Bind("General", "ForceBodyVisible", true,
                "Force your own body meshes to render while supine (first person normally hides/culls them).");
            _freezeHands = Config.Bind("General", "FreezeHandsToWeapon", true,
                "Hands keep the exact grip they had when you went supine and move rigidly with the weapon. Turn off to let the game animate the hands (reload etc.), at the cost of them drifting from the weapon.");
            _backKey    = Config.Bind("Keys", "BackwardKey", KeyCode.S, "Your in-game move-backward key.");
            _forwardKey = Config.Bind("Keys", "ForwardKey", KeyCode.W, "Your in-game move-forward key. Pressing it leaves supine.");
            _enterDelay = Config.Bind("Tuning", "EnterDelaySeconds", 0.35f, "How long backward must be held before going supine.");
            _pivotHeight = Config.Bind("Tuning", "PivotHeight", 0.12f, "Height above ground of the flip axis. Raise if the body clips the floor, lower if it floats.");
            _cameraHeight = Config.Bind("Tuning", "CameraHeight", 0.7f, "Camera height above the ground (m) while supine. The log prints your current prone camera height; pick something between that and your crouch camera height (default 0.7 is a first guess). Hands and weapon follow the camera.");
            _cameraForward = Config.Bind("Tuning", "CameraForward", 0.1f, "Extra meters the camera + hands + weapon move forward (toward your stomach) while supine.");
            _transition = Config.Bind("Tuning", "TransitionSeconds", 0.7f, "How long the roll-over and the camera/hands move take. Higher = smoother.");
            _armForwardShift = Config.Bind("Tuning", "ArmForwardShift", 0.0f, "Extra forward shift (m) applied to the arms/weapon. Tune in game.");
            _armHeightShift = Config.Bind("Tuning", "ArmHeightShift", 0.0f, "Extra height (m) applied to the arms/weapon (on top of CameraHeight, which they already follow).");
        }

        private void LateUpdate()
        {
            var world = Singleton<GameWorld>.Instance;
            var player = world != null ? world.MainPlayer : null;

            // BUG FIX: cache bones whenever the player changes (or bones are missing),
            // NOT only while prone. Previously _player was overwritten while standing,
            // so CacheBones() never ran and _pelvis stayed null -> Apply() never executed.
            if (player != _player)
            {
                RestoreRenderers();
                _player = player;
                _pelvis = _head = null;
                _supine = false;
            }
            if (_player != null && _pelvis == null) CacheBones();

            if (_player == null || !_player.IsInPronePose)
            {
                _supine = false; _backHeld = 0f; _t = 0f; _lockActive = false; _haveLast = false;
                RestoreRenderers();
                return;
            }
            if (_pelvis == null) return;

            UpdateState();

            // Animate toward the target so nothing teleports or blinks.
            float step = Time.deltaTime / Mathf.Max(0.05f, _transition.Value);
            _t = Mathf.MoveTowards(_t, _supine ? 1f : 0f, step);

            if (_t <= 0f) { _loggedCam = false; _loggedHands = false; _loggedWeapon = false; _armCaptured = false; _lockActive = false; _haveLast = false; }
            if (_t > 0f)
            {
                float k = _t * _t * (3f - 2f * _t); // smoothstep
                Apply(k);
                // Body stays hidden while the camera/hands ease in (or out); shown only once fully supine.
                MakeBodyVisible(_supine && _t >= 0.999f);
            }
            else
            {
                RestoreRenderers();
            }
        }

        private void UpdateState()
        {
            if (Input.GetKeyDown(_toggleKey.Value)) { _supine = !_supine; return; }

            if (Input.GetKey(_forwardKey.Value)) { _supine = false; _backHeld = 0f; return; }

            if (_autoEnter.Value && Input.GetKey(_backKey.Value))
            {
                _backHeld += Time.deltaTime;
                if (_backHeld >= _enterDelay.Value) _supine = true;
            }
            else if (!Input.GetKey(_backKey.Value))
            {
                _backHeld = 0f;
            }
        }

        private void CacheBones()
        {
            _pelvis = _head = null;
            _collars.Clear();
            _keep.Clear();
            _weaponT = null;
            _renderers.Clear();
            _origShadow.Clear();
            _loggedRenderers = false;
            if (_player == null) return;

            foreach (var t in _player.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                if (_pelvis == null && n == "Base HumanPelvis") _pelvis = t;
                else if (_head == null && n == "Base HumanHead") _head = t;
                else if (n.StartsWith("Base Human") && (n.Contains("Collarbone") || n.Contains("Clavicle")))
                    _collars.Add(t);
            }

            _renderers.AddRange(_player.GetComponentsInChildren<SkinnedMeshRenderer>(true));

            // Arms (collars) + every weapon root keep their original world pose.
            int weaponRoots = 0;
            _keep.AddRange(_collars);
            foreach (var t in _player.GetComponentsInChildren<Transform>(true))
            {
                string ln = t.name.ToLowerInvariant();
                if (ln.Contains("weapon_root") || ln.Contains("weaponroot"))
                {
                    if (!_keep.Contains(t)) _keep.Add(t);
                    weaponRoots++;
                    Logger.LogInfo($"Supine weapon root: {t.name} parent={(t.parent != null ? t.parent.name : "-")}");
                }
            }
            _keep.Sort((a, b) => Depth(a).CompareTo(Depth(b)));

            // Whole arm chains (collarbone -> hand -> fingers), parent-first.
            _arm.Clear(); _armCaptured = false;
            foreach (var c in _collars)
                foreach (var t in c.GetComponentsInChildren<Transform>(true))
                    if (!_arm.Contains(t)) _arm.Add(t);
            _arm.Sort((a, b) => Depth(a).CompareTo(Depth(b)));

            Logger.LogInfo($"Supine keep-set: collars={_collars.Count} weaponRoots={weaponRoots} armBones={_arm.Count}");

            Logger.LogInfo($"Supine bones: pelvis={_pelvis != null} head={_head != null} collars={_collars.Count} skinnedRenderers={_renderers.Count}");
        }

        // The held weapon is a separate GameObject (hands controller), not part of the skeleton,
        // so lifting the camera/hands left it behind. Track it and lock it like the arms.
        private Transform FindWeaponTransform()
        {
            try
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var hcProp = _player.GetType().GetProperty("HandsController", F);
                object hc = hcProp != null ? hcProp.GetValue(_player, null) : null;
                if (hc == null) return null;
                var gp = hc.GetType().GetProperty("ControllerGameObject", F);
                object v = gp != null ? gp.GetValue(hc, null) : null;
                var tr = v as Transform; if (tr != null) return tr;
                var go = v as GameObject; if (go != null) return go.transform;
                var c = v as Component; return c != null ? c.transform : null;
            }
            catch { return null; }
        }

        private void SyncWeaponTransform()
        {
            if (_weaponT != null && Time.frameCount % 15 != 0) return;
            var wt = FindWeaponTransform();
            if (wt == _weaponT) return;

            if (_weaponT != null) _keep.Remove(_weaponT);
            _weaponT = wt;
            if (_weaponT != null)
            {
                if (!_keep.Contains(_weaponT)) _keep.Add(_weaponT);
                string chain = ""; var tt = _weaponT;
                for (int i = 0; i < 10 && tt != null; i++) { chain += tt.name + " < "; tt = tt.parent; }
                Logger.LogInfo("Supine weapon transform: " + chain);
            }
            _keep.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
            _wRend.Clear();
            _loggedWeapon = false;
            _armCaptured = false;
        }

        private void RefreshWeaponRenderers()
        {
            _wRend.Clear();
            foreach (var t in _keep)
            {
                if (t == null || t.name != "Weapon_root") continue; // first-person root only
                _wRend.AddRange(t.GetComponentsInChildren<Renderer>(true));
            }
            if (_weaponT != null) _wRend.AddRange(_weaponT.GetComponentsInChildren<Renderer>(true));
        }

        // The first-person weapon must never be hidden while supine.
        private void EnsureWeaponVisible(float k)
        {
            if (_wRend.Count == 0 || Time.frameCount % 30 == 0) RefreshWeaponRenderers();

            foreach (var r in _wRend)
            {
                if (r == null) continue;
                r.forceRenderingOff = false;
                if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                {
                    if (!_wOrig.ContainsKey(r)) _wOrig[r] = r.shadowCastingMode;
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
                var sk = r as SkinnedMeshRenderer;
                if (sk != null) sk.updateWhenOffscreen = true;
            }

            if (k >= 0.999f && !_loggedWeapon)
            {
                _loggedWeapon = true;
                var cam = Camera.main;
                Logger.LogInfo($"Supine weapon renderers: count={_wRend.Count}");
                int n = 0;
                foreach (var r in _wRend)
                {
                    if (r == null || n++ >= 15) continue;
                    string rel = cam != null ? cam.transform.InverseTransformPoint(r.bounds.center).ToString("F2") : "?";
                    Logger.LogInfo($"Supine weapon renderer: {r.name} type={r.GetType().Name} enabled={r.enabled} goActive={r.gameObject.activeInHierarchy} layer={LayerMask.LayerToName(r.gameObject.layer)} shadow={r.shadowCastingMode} visible={r.isVisible} camRel={rel}");
                }
                if (_wRend.Count > 0 && _wRend[0] != null)
                {
                    string chain = ""; var tt = _wRend[0].transform;
                    for (int i = 0; i < 12 && tt != null; i++) { chain += tt.name + " < "; tt = tt.parent; }
                    Logger.LogInfo("Supine weapon chain: " + chain);
                }
            }
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            while (t != null && t.parent != null) { d++; t = t.parent; }
            return d;
        }

        // The skeleton is moved AFTER the animator computed the renderers' bounds, so the
        // bounds still sit where the body used to be (behind the camera) and Unity culls the mesh.
        // Also first person can put body meshes in ShadowsOnly, so force them to draw.
        private void MakeBodyVisible(bool showBody)
        {
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                r.updateWhenOffscreen = true;
                if (r.name.StartsWith("Hands")) continue; // first-person arms always stay visible

                if (!_origShadow.ContainsKey(r)) _origShadow[r] = r.shadowCastingMode;

                if (!showBody) { r.forceRenderingOff = true; continue; }

                r.forceRenderingOff = false;
                if (_forceBodyVisible.Value)
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                    r.enabled = true;
                }
            }

            if (showBody && !_loggedRenderers)
            {
                _loggedRenderers = true;
                foreach (var r in _renderers)
                    if (r != null)
                        Logger.LogInfo($"Supine renderer: {r.name} layer={LayerMask.LayerToName(r.gameObject.layer)} orig-shadow={(_origShadow.TryGetValue(r, out var sh) ? sh.ToString() : "?")} visible={r.isVisible}");
            }
        }

        private void RestoreRenderers()
        {
            if (_wOrig.Count > 0)
            {
                foreach (var kv in _wOrig)
                    if (kv.Key != null) kv.Key.shadowCastingMode = kv.Value;
                _wOrig.Clear();
            }
            if (_origShadow.Count == 0) return;
            foreach (var kv in _origShadow)
            {
                if (kv.Key == null) continue;
                kv.Key.shadowCastingMode = kv.Value;
                kv.Key.forceRenderingOff = false;
                kv.Key.updateWhenOffscreen = false;
            }
            _origShadow.Clear();
        }

        private void Apply(float k)
        {
            Transform root = _player.gameObject.transform;
            Vector3 right = root.right;

            // Flip axis passes through the head, so the head stays under the camera
            // and the rest of the body swings out in FRONT of it, face up.
            Vector3 pivot = _head != null ? _head.position : root.position;
            pivot.y = root.position.y + _pivotHeight.Value;

            // Flip fully right away: the body is hidden during the transition, so no roll-over is seen.
            Quaternion flip = Quaternion.AngleAxis(180f, right);

            SyncWeaponTransform();

            // 1) Snapshot the arms + weapon in their original world pose (what the player sees prone).
            _keepPos.Clear(); _keepRot.Clear();
            foreach (var t in _keep)
            {
                _keepPos.Add(t != null ? t.position : Vector3.zero);
                _keepRot.Add(t != null ? t.rotation : Quaternion.identity);
            }

            // 2) Rotate the whole skeleton around the pivot (head/torso/legs).
            _pelvis.position = pivot + flip * (_pelvis.position - pivot);
            _pelvis.rotation = flip * _pelvis.rotation;

            // 3) Put arms + weapon back exactly where they were (parents first), following the camera lift.
            //    Everything under them (hands, palms, weapon model) keeps its local pose, so the IK'd
            //    grip is preserved and the weapon sits in front of you like in the normal prone view.
            // Camera goes to an absolute height above the ground (between prone and crouch),
            // eased in with k so it never snaps. Hands/weapon get the same offset.
            var cam = Camera.main;
            float dy = 0f;
            Vector3 basePos = cam != null ? cam.transform.position : Vector3.zero;
            if (cam != null)
            {
                // If the game did NOT rewrite the camera since we last moved it, our own offset is
                // still in it: fall back to the unmodified base so the offset never stacks or fights.
                if (_haveLast && (basePos - _lastSet).sqrMagnitude < 1e-8f) basePos = _lastBase;
                float camRel = basePos.y - root.position.y;
                dy = (root.position.y + _cameraHeight.Value) - basePos.y;
                if (!_loggedCam)
                {
                    _loggedCam = true;
                    Logger.LogInfo($"Supine camera: height above ground now={camRel:F2} m, target={_cameraHeight.Value:F2} m");
                }
            }
            Vector3 camOffset = (Vector3.up * dy + root.forward * _cameraForward.Value) * k;
            Vector3 offset = camOffset
                           + (Vector3.up * _armHeightShift.Value + root.forward * _armForwardShift.Value) * k;
            for (int i = 0; i < _keep.Count; i++)
            {
                var t = _keep[i];
                if (t == null) continue;
                t.rotation = _keepRot[i];
                t.position = _keepPos[i] + offset;
            }

            if (cam != null)
            {
                cam.transform.position = basePos + camOffset;
                _lastBase = basePos; _lastSet = cam.transform.position; _haveLast = true;

                // Remember where arms + weapon sit relative to the (final) camera and lock them there.
                Vector3 cp = cam.transform.position; Quaternion inv = Quaternion.Inverse(cam.transform.rotation);
                _relPos.Clear(); _relRot.Clear();
                for (int i = 0; i < _keep.Count; i++)
                {
                    var t = _keep[i];
                    _relPos.Add(t != null ? inv * (t.position - cp) : Vector3.zero);
                    _relRot.Add(t != null ? inv * t.rotation : Quaternion.identity);
                }
                _lockActive = true;

                if (k >= 0.999f && !_loggedHands)
                {
                    _loggedHands = true;
                    for (int i = 0; i < _keep.Count; i++)
                        if (_keep[i] != null && _keep[i].name == "Weapon_root")
                            Logger.LogInfo($"Supine hands: Weapon_root camera-relative = {_relPos[i].x:F3}, {_relPos[i].y:F3}, {_relPos[i].z:F3}");
                }
            }

            CaptureArms();
            ApplyArmLock();
            EnsureWeaponVisible(k);
        }
    }
}
