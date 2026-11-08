using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DotaGlass
{
    /// <summary>
    /// Controls the volume of the app that is playing music (its Windows mixer slider), so you can
    /// balance music against Dota without touching the game's sound. Falls back to the master volume
    /// when the media app can't be matched to an audio session.
    /// </summary>
    internal sealed class AppVolume
    {
        static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        const int CLSCTX_ALL = 0x17;

        IMMDeviceEnumerator enumerator;
        readonly List<ISimpleAudioVolume> targets = new List<ISimpleAudioVolume>();
        IAudioEndpointVolume master;
        string boundAumid = "";

        public bool IsMaster { get { return targets.Count == 0; } }

        /// <summary>Resolve which audio sessions belong to the media app (cheap; called on show / session change).</summary>
        public void Bind(string aumid)
        {
            boundAumid = aumid ?? "";
            Release();
            try
            {
                if (enumerator == null) enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device) != 0 || device == null) return;

                if (boundAumid.Length > 0)
                {
                    object o;
                    Guid iid = IID_IAudioSessionManager2;
                    if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) == 0)
                    {
                        var mgr = (IAudioSessionManager2)o;
                        IAudioSessionEnumerator sessions;
                        if (mgr.GetSessionEnumerator(out sessions) == 0 && sessions != null)
                        {
                            int count;
                            sessions.GetCount(out count);
                            for (int i = 0; i < count; i++)
                            {
                                object so;
                                if (sessions.GetSession(i, out so) != 0 || so == null) continue;
                                var ctl = so as IAudioSessionControl2;
                                uint pid;
                                if (ctl == null || ctl.GetProcessId(out pid) != 0 || pid == 0) { Marshal.ReleaseComObject(so); continue; }
                                string exe = Native.GetProcessFileName(pid);
                                var vol = so as ISimpleAudioVolume;
                                if (vol != null && exe != null && Matches(boundAumid, exe)) targets.Add(vol);
                                else Marshal.ReleaseComObject(so);
                            }
                            Marshal.ReleaseComObject(sessions);
                        }
                        Marshal.ReleaseComObject(mgr);
                    }
                }

                if (targets.Count == 0)
                {
                    object o;
                    Guid iid = IID_IAudioEndpointVolume;
                    if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) == 0) master = (IAudioEndpointVolume)o;
                }
                Marshal.ReleaseComObject(device);
            }
            catch { }
        }

        /// <summary>0..1, or 0.5 if unknown.</summary>
        public double Get()
        {
            try
            {
                float v;
                if (targets.Count > 0 && targets[0].GetMasterVolume(out v) == 0) return v;
                if (master != null && master.GetMasterVolumeLevelScalar(out v) == 0) return v;
            }
            catch { }
            return 0.5;
        }

        public void Set(double value)
        {
            float v = (float)Math.Max(0, Math.Min(1, value));
            Guid ctx = Guid.Empty;
            bool failed = false;
            try
            {
                if (targets.Count > 0)
                {
                    foreach (var t in targets) if (t.SetMasterVolume(v, ref ctx) != 0) failed = true;
                }
                else if (master != null) failed = master.SetMasterVolumeLevelScalar(v, ref ctx) != 0;
                else failed = true;
            }
            catch { failed = true; }

            if (failed)
            {
                // session/device went away (e.g. headphones unplugged) - rebind once and retry
                Bind(boundAumid);
                try
                {
                    foreach (var t in targets) t.SetMasterVolume(v, ref ctx);
                    if (targets.Count == 0 && master != null) master.SetMasterVolumeLevelScalar(v, ref ctx);
                }
                catch { }
            }
        }

        void Release()
        {
            foreach (var t in targets) try { Marshal.ReleaseComObject(t); } catch { }
            targets.Clear();
            if (master != null) { try { Marshal.ReleaseComObject(master); } catch { } master = null; }
        }

        /// <summary>Match a media AUMID (e.g. "Spotify.exe", "SpotifyAB.SpotifyMusic_..!Spotify", "Chrome", "MSEdge") to a process.</summary>
        static bool Matches(string aumid, string exe)
        {
            string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
            name = name.ToLowerInvariant();
            string a = aumid.ToLowerInvariant();
            if (name.Length >= 4 && a.Contains(name)) return true;
            if (name == "firefox" && MediaController.IsHex(aumid)) return true; // Firefox uses a hashed AUMID
            if (name == "microsoft.media.player" && a.Contains("zunemusic")) return true;
            return false;
        }

        // ---------------------------------------------------------------- Core Audio COM interop

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(IntPtr param, IntPtr ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6F017C2EBE6E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        }
    }
}
