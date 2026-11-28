using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using static Native;

// The spike service. It runs as LocalSystem, starts the probe in each session of the user in the config, reads the
// lines, reads each entry as design D3b states, and logs it. Config: C:\ProgramData\probespike\host.conf, key=value:
//   user=Kid  debugPriv=on|off  writeRestricted=on|off  create=detached|nowindow
//   restart=on|off (restart after an end with 1, 2, 5 s; watchdog 30 s without a line)
//   Only in a build with VM_TEST (dotnet publish -p:VmTest=true, VM 102 only):
//   probeArgs=<args> (test switches of the probe)  channel=on|off (task 1.1: a client as the user and one as SYSTEM)
// Log: C:\ProgramData\probespike\host.log.
//
// Safety (task 9.1, the child's PC): before it reads or writes anything, the service creates its folder with an ACL
// for SYSTEM and Administrators only (owner SYSTEM, protected, no inheritance). It refuses to start if the folder
// exists with another owner, an inherited ACL or an entry for another account, or if its own install folder or exe
// can be changed by an account that is not SYSTEM, Administrators or TrustedInstaller. It starts the probe only from
// its own install folder. So a standard user cannot make this SYSTEM service run an exe of its choice. A folder that
// an older spike build left behind fails the check: delete it as an administrator, and the service creates it again.
internal sealed class Host : ServiceBase
{
    private const string Dir = @"C:\ProgramData\probespike";
    private const string ExeName = "ProbeSpike.exe";

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier CreatorOwnerSid = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    // Rights that let an account change a file or folder (GENERIC_WRITE and GENERIC_ALL for inherit-only entries).
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
        (FileSystemRights)0x40000000 | (FileSystemRights)0x10000000;

    private static StreamWriter? _log;
    private Dictionary<string, string> _conf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ProbeRun> _runs = new();
    private readonly object _gate = new();
    private readonly List<DateTime> _unexpected = new();
    private Timer? _timer;
    private bool _stopping;

    public Host()
    {
        ServiceName = "ProbeSpike";
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        CanStop = true;
    }

    public static void Log(string s)
    {
        lock (typeof(Host)) _log?.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} tick={GetTickCount64()} {s}");
    }

    private string C(string k, string def) => _conf.TryGetValue(k, out var v) ? v : def;

    protected override void OnStart(string[] args)
    {
        // Throw before any read or write. ServiceBase writes the message to the Application event log.
        var installProblem = InstallProblem();
        if (installProblem != null) throw new InvalidOperationException($"ProbeSpike refuses to start: {installProblem}");
        SecureDataFolder();
        _log =new StreamWriter(new FileStream(Path.Combine(Dir, "host.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var conf = Path.Combine(Dir, "host.conf");
        if (File.Exists(conf))
            foreach (var l in File.ReadAllLines(conf))
                if (l.Contains('=')) _conf[l[..l.IndexOf('=')].Trim()] = l[(l.IndexOf('=') + 1)..].Trim();
        Log($"service start conf=[{string.Join("; ", _conf.Select(kv => kv.Key + "=" + kv.Value))}] vmTest={VmTest}");
        if (!VmTest && (_conf.ContainsKey("probeArgs") || _conf.ContainsKey("channel")))
            Log("probeArgs and channel are ignored: this build has no VM_TEST");
        if (C("debugPriv", "on") == "on") Log($"SeDebugPrivilege: {EnablePrivilege("SeDebugPrivilege")}");
        if (C("debugPriv", "on") == "disable") Log($"SeDebugPrivilege disable: {EnablePrivilege("SeDebugPrivilege", 0)}");
        foreach (var s in Sessions()) if (s.user.Equals(C("user", "Kid"), StringComparison.OrdinalIgnoreCase)) StartProbe(s.id, "service start");
        _timer = new Timer(_ => Tick(), null, 60_000, 60_000);
        _watchdog = new Timer(_ => Watchdog(), null, 5_000, 5_000);
    }

#if VM_TEST
    private const bool VmTest = true;
#else
    private const bool VmTest = false;
#endif

    // ---------- folder safety (task 9.1) ----------

    // Creates Dir with owner SYSTEM and a protected DACL for SYSTEM and Administrators only, or checks the Dir that exists.
    private static void SecureDataFolder()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", 1, out var sd, out _))
            throw new Win32Exception();
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
        var created = CreateDirectoryW(Dir, ref sa);
        var err = created ? 0 : Marshal.GetLastWin32Error();
        LocalFree(sd);
        if (!created && err != 183 /* ERROR_ALREADY_EXISTS */) throw new Win32Exception(err, $"ProbeSpike cannot create {Dir}");
        var problem = DataFolderProblem();
        if (problem != null)
            throw new InvalidOperationException($"ProbeSpike refuses to start: {Dir} {problem}. Delete it as an administrator; the service creates it again.");
    }

    // The folder and each file in it: no link, owner SYSTEM or Administrators, allow entries only for these two.
    // The folder itself must also have a protected DACL without inherited entries.
    private static string? DataFolderProblem()
    {
        try
        {
            var dir = new DirectoryInfo(Dir);
            var problem = AclProblem(dir, dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), folder: true);
            if (problem != null) return problem;
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                FileSystemSecurity sec = entry is DirectoryInfo d
                    ? d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                    : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                problem = AclProblem(entry, sec, folder: false);
                if (problem != null) return $"has {entry.Name}, which {problem}";
            }
            return null;
        }
        catch (Exception ex) { return $"cannot be checked ({ex.GetType().Name}: {ex.Message})"; }
    }

    private static string? AclProblem(FileSystemInfo item, FileSystemSecurity sec, bool folder)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return "is a link";
        var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || (owner != SystemSid && owner != AdminsSid)) return $"has the owner {owner}";
        if (folder && !sec.AreAccessRulesProtected) return "inherits its ACL";
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if (folder && r.IsInherited) return "has an inherited entry";
            var sid = (SecurityIdentifier)r.IdentityReference;
            if (sid != SystemSid && sid != AdminsSid) return $"gives access to {sid}";
        }
        return null;
    }

    // The install folder and the exe: owner SYSTEM, Administrators or TrustedInstaller, and no other account (except
    // CREATOR OWNER, which applies only to items that an account creates there) has a right to change them.
    private static string? InstallProblem()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            var exe = new FileInfo(Path.Combine(dir.FullName, ExeName));
            var problem = InstallAclProblem(dir, dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            if (problem != null) return $"the install folder {dir.FullName} {problem}";
            problem = InstallAclProblem(exe, exe.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            return problem == null ? null : $"{exe.FullName} {problem}";
        }
        catch (Exception ex) { return $"the install folder cannot be checked ({ex.GetType().Name}: {ex.Message})"; }
    }

    private static string? InstallAclProblem(FileSystemInfo item, FileSystemSecurity sec)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return "is a link";
        var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || (owner != SystemSid && owner != AdminsSid && owner != TrustedInstallerSid)) return $"has the owner {owner}";
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow || (r.FileSystemRights & WriteRights) == 0) continue;
            var sid = (SecurityIdentifier)r.IdentityReference;
            if (sid != SystemSid && sid != AdminsSid && sid != TrustedInstallerSid && sid != CreatorOwnerSid)
                return $"can be changed by {sid}";
        }
        return null;
    }

    private Timer? _watchdog;
    private readonly Dictionary<int, long> _sessionLastT = new();

    // Watchdog (design D2, S15): no accepted line for 30 s in an active session ends the probe.
    private void Watchdog()
    {
        if (C("watchdog", "on") != "on") return;
        lock (_gate)
            foreach (var r in _runs.Values)
            {
                var ago = Environment.TickCount64 - r.LastLine;
                if (ago > 30_000 && ConnectState(r.Session) == 0 && !r.Killed)
                {
                    r.Killed = true;
                    Log($"WATCHDOG: no line for {ago} ms, TerminateProcess probe pid={r.Pid} ok={TerminateProcess(r.Process, 1)}");
                }
            }
    }

    protected override void OnStop()
    {
        _stopping = true;
        lock (_gate) foreach (var r in _runs.Values) { r.Planned = true; TerminateProcess(r.Process, 0); }
        Log("service stop");
    }

    protected override void OnSessionChange(SessionChangeDescription d)
    {
        Log($"session change {d.Reason} session={d.SessionId} user={SessionUser(d.SessionId)}");
        switch (d.Reason)
        {
            case SessionChangeReason.SessionLogon:
            case SessionChangeReason.SessionUnlock:
                if (SessionUser(d.SessionId).Equals(C("user", "Kid"), StringComparison.OrdinalIgnoreCase)) StartProbe(d.SessionId, d.Reason.ToString());
                break;
            case SessionChangeReason.SessionLogoff:
                lock (_gate)
                    if (_runs.TryGetValue(d.SessionId, out var r))
                    {
                        r.Planned = true;
                        Log($"logoff: TerminateProcess probe pid={r.Pid} ok={TerminateProcess(r.Process, 0)}");
                        _runs.Remove(d.SessionId);
                    }
                break;
        }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus s)
    {
        QueryUnbiasedInterruptTime(out var u);
        Log($"power {s} unbiasedMs={u / 10000}");
        lock (_gate) foreach (var r in _runs.Values) r.ResumeAt = s is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend ? Environment.TickCount64 : r.ResumeAt;
        return true;
    }

    private void Tick()
    {
        lock (_gate)
            foreach (var r in _runs.Values)
            {
                GetProcessHandleCount(r.Process, out var hc);
                Log($"tick probe pid={r.Pid} session={r.Session} handles={hc} lines={r.Lines} lastLineAgoMs={Environment.TickCount64 - r.LastLine}");
            }
        foreach (var s in Sessions())
            if (s.user.Equals(C("user", "Kid"), StringComparison.OrdinalIgnoreCase))
                lock (_gate) if (!_runs.ContainsKey(s.id)) StartProbe(s.id, "tick");
    }

    private static List<(int id, string user)> Sessions()
    {
        var list = new List<(int, string)>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var info, out var count)) return list;
        var size = Marshal.SizeOf<WTS_SESSION_INFO>();
        for (var i = 0; i < count; i++)
        {
            var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(info + i * size);
            list.Add((si.SessionId, SessionUser(si.SessionId)));
        }
        WTSFreeMemory(info);
        return list;
    }

    public static string SessionUser(int session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, 5 /* WTSUserName */, out var buf, out _)) return "";
        var s = Marshal.PtrToStringUni(buf) ?? "";
        WTSFreeMemory(buf);
        return s;
    }

    public static int ConnectState(int session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, 8 /* WTSConnectState */, out var buf, out _)) return -1;
        var s = Marshal.ReadInt32(buf);
        WTSFreeMemory(buf);
        return s;
    }

    // ---------- probe start (design D2) ----------

    private void StartProbe(int session, string why)
    {
        lock (_gate)
        {
            if (_stopping || _runs.ContainsKey(session)) return;
            try
            {
#if VM_TEST
                if (C("channel", "off") == "on") ChannelTest(session);
#endif
                var run = LaunchProbe(session);
                _runs[session] = run;
                Log($"probe started ({why}) session={session} pid={run.Pid} handle={run.ChildHandle}");
                new Thread(() => ReadLoop(run)) { IsBackground = true, Priority = ThreadPriority.Highest }.Start();
                ThreadPool.RegisterWaitForSingleObject(new ProcessWait(run.Process), (_, _) => OnProbeEnd(run), null, -1, true);
            }
            catch (Exception ex) { Log($"probe start failed session={session}: {ex.Message}"); }
        }
    }

    private sealed class ProcessWait : WaitHandle { public ProcessWait(IntPtr h) { SafeWaitHandle = new SafeWaitHandle(h, false); } }

    private void OnProbeEnd(ProbeRun run)
    {
        GetExitCodeProcess(run.Process, out var code);
        var sinceResume = Environment.TickCount64 - run.ResumeAt;
        Log($"probe end pid={run.Pid} session={run.Session} exit=0x{code:X} planned={run.Planned} lines={run.Lines} sinceResumeMs={sinceResume}");
        lock (_gate)
        {
            if (_runs.TryGetValue(run.Session, out var cur) && cur == run) _runs.Remove(run.Session);
            if (run.Planned || _stopping || C("restart", "on") != "on") return;
            var now = DateTime.UtcNow;
            _unexpected.Add(now);
            _unexpected.RemoveAll(t => now - t > TimeSpan.FromMinutes(10));
            var n = _unexpected.Count;
            var delay = n <= 1 ? 1 : n == 2 ? 2 : 5;
            Log($"PROBE_RESTART unexpected end #{n} in 10 min; restart in {delay} s{(n >= 3 ? "; budget item" : "")}");
            var session = run.Session;
            new Timer(_ => StartProbe(session, "restart"), null, delay * 1000, Timeout.Infinite);
        }
    }

    private ProbeRun LaunchProbe(int session)
    {
        var sa = SystemOnly();
        if (!CreatePipe(out var read, out var write, ref sa, 0)) throw new Win32Exception();
        Log($"anonymous pipe DACL read={Sddl(read)} write={Sddl(write)}");
        var token = SystemTokenForSession(session, C("writeRestricted", "off") == "on");
        Log($"probe token privileges={TokenPrivilegeCount(token)} user={TokenUserSid(token)}");
        var env = EnvBlock(token, null);
        // Only the exe of the install folder that InstallProblem checked at the start.
        var exe = Path.Combine(AppContext.BaseDirectory, ExeName);
        var flags = C("create", "detached") == "nowindow" ? CREATE_NO_WINDOW : DETACHED_PROCESS;
#if VM_TEST
        var probeArgs = "probe --pipe {0} " + C("probeArgs", "").Replace("{", "").Replace("}", "");
#else
        const string probeArgs = "probe --pipe {0}";
#endif
        var pi = Launch(token, write, exe, probeArgs, env, flags | HIGH_PRIORITY_CLASS, systemOnlyObjects: true, out var childHandle);
        CloseHandle(write);
        Log($"probe process DACL={Sddl(pi.hProcess)} thread DACL={Sddl(pi.hThread)}");
        CloseHandle(pi.hThread);
        CloseHandle(token);
        return new ProbeRun { Session = session, Pid = pi.dwProcessId, Process = pi.hProcess, Read = read, ChildHandle = childHandle, LastLine = Environment.TickCount64 };
    }

    public static IntPtr SystemTokenForSession(int session, bool writeRestricted)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out var own)) throw new Win32Exception();
        SID_AND_ATTRIBUTES[]? restrict = null;
        if (writeRestricted)
        {
            ConvertStringSidToSidW("S-1-5-18", out var sys);
            restrict = new[] { new SID_AND_ATTRIBUTES { Sid = sys } };
        }
        if (!CreateRestrictedToken(own, DISABLE_MAX_PRIVILEGE | (writeRestricted ? WRITE_RESTRICTED : 0), 0, IntPtr.Zero, 0, IntPtr.Zero,
                restrict?.Length ?? 0, restrict, out var restricted)) throw new Win32Exception();
        if (!DuplicateTokenEx(restricted, TOKEN_ALL_ACCESS, IntPtr.Zero, 2, 1 /* TokenPrimary */, out var primary)) throw new Win32Exception();
        CloseHandle(restricted);
        CloseHandle(own);
        var p = Marshal.AllocHGlobal(4);
        Marshal.WriteInt32(p, session);
        if (!SetTokenInformation(primary, TokenSessionId, p, 4)) throw new Win32Exception();
        Marshal.FreeHGlobal(p);
        // SYSTEM-only default DACL.
        ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;SY)", 1, out var sd, out _);
        GetSecurityDescriptorDacl(sd, out _, out var dacl, out _);
        var dd = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(dd, dacl);
        if (!SetTokenInformation(primary, TokenDefaultDacl, dd, IntPtr.Size)) throw new Win32Exception();
        Marshal.FreeHGlobal(dd);
        return primary;
    }

    // The environment of the token, without DOTNET_, COMPlus_, CORECLR_, WEBVIEW2_; for SYSTEM with the two settings.
    public static IntPtr EnvBlock(IntPtr token, string? userForLog)
    {
        if (!CreateEnvironmentBlock(out var block, token, false)) throw new Win32Exception();
        var vars = new List<string>();
        var p = block;
        while (true)
        {
            var s = Marshal.PtrToStringUni(p) ?? "";
            if (s.Length == 0) break;
            vars.Add(s);
            p += (s.Length + 1) * 2;
        }
        DestroyEnvironmentBlock(block);
        var removed = vars.Where(Strip).ToList();
        vars.RemoveAll(Strip);
        if (userForLog is null) { vars.Add("DOTNET_EnableDiagnostics=0"); vars.Add("DOTNET_DISABLE_GUI_ERRORS=1"); }
        Log($"environment ({userForLog ?? "SYSTEM"}): removed [{string.Join(", ", removed.Select(r => r.Split('=')[0]))}]");
        var text = string.Join("\0", vars) + "\0\0";
        return Marshal.StringToHGlobalUni(text);
        static bool Strip(string v) =>
            v.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || v.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) || v.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase);
    }

    // CreateProcessAsUserW with one inherited handle (PROC_THREAD_ATTRIBUTE_HANDLE_LIST). The command line gets the
    // value of the inheritable duplicate at {0}.
    public static PROCESS_INFORMATION Launch(IntPtr token, IntPtr handle, string exe, string args, IntPtr env, uint flags, bool systemOnlyObjects, out long childHandle)
    {
        if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var dup, 0, true, DUPLICATE_SAME_ACCESS)) throw new Win32Exception();
        childHandle = (long)dup;
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var hv = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
            Marshal.WriteIntPtr(hv, dup);
            if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, hv, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var si = new STARTUPINFOEX { lpAttributeList = list };
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.lpDesktop = @"winsta0\default";
            var pa = systemOnlyObjects ? SystemOnly() : new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            var ta = pa;
            var cmd = new StringBuilder($"\"{exe}\" " + string.Format(args, (long)dup));
            if (!CreateProcessAsUserW(token, exe, cmd, ref pa, ref ta, true, flags | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, env,
                    Path.GetDirectoryName(exe), ref si, out var pi))
                throw new Win32Exception();
            return pi;
        }
        finally
        {
            CloseHandle(dup);
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(hv);
            Marshal.FreeHGlobal(env);
        }
    }

    // ---------- task 1.1: a private named pipe for a client of the user and one of SYSTEM ----------

    private void ChannelTest(int session)
    {
        foreach (var asSystem in new[] { false, true })
        {
            try
            {
                var name = @"\\.\pipe\ParentalControl.Agent." + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                var sa = SystemOnly();
                var server = CreateNamedPipeW(name, 0x3 | 0x00080000 | 0x40000000 /* DUPLEX | FIRST_PIPE_INSTANCE | OVERLAPPED */,
                    0x0 | 0x8 /* BYTE | REJECT_REMOTE */, 1, 65536, 65536, 0, ref sa);
                if (server == (IntPtr)(-1)) throw new Win32Exception();
                var client = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, 3, 0x40000000 | 0x00100000 | 0x00010000, IntPtr.Zero);
                if (client == (IntPtr)(-1)) throw new Win32Exception();
                var connected = ConnectNamedPipe(server, IntPtr.Zero) || Marshal.GetLastWin32Error() == 535;
                IntPtr token;
                if (asSystem) token = SystemTokenForSession(session, false);
                else
                {
                    if (!WTSQueryUserToken(session, out var ut)) throw new Win32Exception();
                    DuplicateTokenEx(ut, TOKEN_ALL_ACCESS, IntPtr.Zero, 2, 1, out token);
                    CloseHandle(ut);
                }
                var env = EnvBlock(token, asSystem ? null : "user");
                var exe = Path.Combine(AppContext.BaseDirectory, "ProbeSpike.exe");
                var pi = Launch(token, client, exe, "client --channel {0} name=" + name, env, CREATE_NO_WINDOW, asSystem, out var ch);
                CloseHandle(client);
                CloseHandle(pi.hThread);
                Log($"channel test {(asSystem ? "SYSTEM" : "user")}: client pid={pi.dwProcessId} handle={ch} sid={TokenUserSid(token)} connected={connected} name={name}");
                CloseHandle(token);
                var stream = new NamedPipeServerStream(PipeDirection.InOut, true, true, new SafePipeHandle(server, true));
                var label = asSystem ? "SYSTEM" : "user";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var reader = new StreamReader(stream);
                        var writer = new StreamWriter(stream) { AutoFlush = true };
                        await writer.WriteLineAsync($"hello from the service to {label}");
                        var cts = new CancellationTokenSource(15000);
                        var line = await reader.ReadLineAsync(cts.Token);
                        Log($"channel test {label}: service read [{line}]");
                    }
                    catch (Exception ex) { Log($"channel test {label}: service read failed {ex.Message}"); }
                });
            }
            catch (Exception ex) { Log($"channel test {(asSystem ? "SYSTEM" : "user")} failed: {ex.Message}"); }
        }
    }

    // ---------- reading (design D3, D3b) ----------

    private sealed class ProbeRun
    {
        public int Session, Pid;
        public IntPtr Process, Read;
        public long ChildHandle, LastLine, LastT = -1, ResumeAt = -100000;
        public long LastU = -1;
        public int Lines;
        public volatile bool Planned, Killed;
    }

    private void ReadLoop(ProbeRun run)
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        var buf = new byte[4096];
        var line = new List<byte>(4096);
        var dropping = false;
        var chunk = new byte[1];
        // A blocking ReadFile of one byte at a time keeps the code simple; the volume is small.
        var one = new byte[512];
        while (true)
        {
            if (!ReadFile(run.Read, one, one.Length, out var n, IntPtr.Zero) || n == 0)
            {
                Log($"reader end pid={run.Pid} err={Marshal.GetLastWin32Error()}");
                CloseHandle(run.Read);
                return;
            }
            for (var i = 0; i < n; i++)
            {
                var b = one[i];
                if (b == (byte)'\n')
                {
                    if (!dropping) Handle(run, line.ToArray());
                    else Log("dropped a line over 4 KB");
                    line.Clear();
                    dropping = false;
                }
                else if (!dropping)
                {
                    line.Add(b);
                    if (line.Count > 4096) { dropping = true; line.Clear(); }
                }
            }
        }
    }

    private void Handle(ProbeRun run, byte[] bytes)
    {
        var now = Environment.TickCount64;
        JsonElement root;
        try { root = JsonDocument.Parse(bytes).RootElement; }
        catch (Exception ex) { Log($"bad line ({bytes.Length} B): {ex.Message}"); return; }
        if (root.TryGetProperty("log", out var lg)) { Log($"probe log pid={run.Pid}: {lg.GetString()}"); return; }
        if (!root.TryGetProperty("t", out var tEl) || !tEl.TryGetInt64(out var t)) { Log("line without t"); return; }
        if (t <= run.LastT || t > now) { Log($"t not valid t={t} last={run.LastT} now={now}"); return; }
        var gap = run.LastT < 0 ? -1 : t - run.LastT;
        var u = root.TryGetProperty("u", out var uEl) ? uEl.GetInt64() : -1;
        var ugap = run.LastU < 0 ? -1 : u - run.LastU;
        var sgap = _sessionLastT.TryGetValue(run.Session, out var st) ? t - st : -1;
        _sessionLastT[run.Session] = t;
        if (run.Lines == 0) Log($"first line of probe pid={run.Pid}: time since the last line of the session (any probe) = {sgap} ms");
        run.LastT = t; run.LastU = u; run.LastLine = now; run.Lines++;
        var sb = new StringBuilder();
        sb.Append($"S pid={run.Pid} t={t} gap={gap} ugap={ugap} lag={now - t} len={bytes.Length}");
        var state = ConnectState(run.Session);
        sb.Append($" conn={state}");
        if (root.TryGetProperty("blind", out var bl)) sb.Append($" BLIND={bl.GetString()}");
        if (root.TryGetProperty("deskErr", out var de)) sb.Append($" deskErr={de.GetString()}");
        if (root.TryGetProperty("window", out _)) sb.Append(" window=false");
        if (root.TryGetProperty("desktop", out var dk)) sb.Append($" desk={dk.GetString()}");
        var unreadable = false;
        var ended = new List<string>();
        void E(string role, JsonElement e)
        {
            var (outcome, exe, extra) = ReadEntry(e, run.Session);
            if (outcome == "Unreadable") unreadable = true;
            if (outcome == "Ended") ended.Add(role);
            sb.Append($" {role}={outcome}:{exe}{extra}");
        }
        if (root.TryGetProperty("fg", out var fg)) E("fg", fg);
        if (root.TryGetProperty("focus", out var fo)) E("focus", fo);
        if (root.TryGetProperty("children", out var ch)) foreach (var c in ch.EnumerateArray()) E("child", c);
        if (root.TryGetProperty("cursor", out var cu))
        {
            var na = cu.GetProperty("noActivate").GetBoolean();
            foreach (var c in cu.GetProperty("entries").EnumerateArray()) E(na ? "cursorNA" : "cursor", c);
        }
        foreach (var k in new[] { "cls", "hung", "vis", "steps", "chainProcs", "curCls", "dur", "cms" })
            if (root.TryGetProperty(k, out var v)) sb.Append($" {k}={v}");
        if (root.TryGetProperty("title", out var ti)) sb.Append($" title=[{ti.GetString()}]");
        if (unreadable) sb.Append(" => BLIND(unreadable)");
        if (ended.Count > 0) sb.Append($" => ENDED({string.Join(",", ended)})");
        Log(sb.ToString());
    }

    private static (string outcome, string exe, string extra) ReadEntry(JsonElement e, int session)
    {
        var tid = e.GetProperty("tid").GetInt32();
        var pid = e.GetProperty("pid").GetInt32();
        var th = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, tid);
        if (th == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            return (err == 87 ? "Ended" : "Unreadable", $"pid{pid}", $"(OpenThread {err})");
        }
        var owner = GetProcessIdOfThread(th);
        CloseHandle(th);
        if (owner != pid) return ("Ended", $"pid{pid}", $"(thread of {owner})");
        var ph = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (ph == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            return (err == 87 ? "Ended" : "Unreadable", $"pid{pid}", $"(OpenProcess {err})");
        }
        try
        {
            ProcessIdToSessionId(pid, out var s);
            var exe = Exe(ph);
            if (s != session) return ("Unreadable", exe, $"(session {s})");
            var extra = "";
            if (Path.GetFileName(exe).Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)) extra = "(chain " + Chain(pid) + ")";
            return ("Read", Path.GetFileName(exe), extra);
        }
        finally { CloseHandle(ph); }
    }

    public static string Exe(IntPtr ph)
    {
        var buf = new char[1024];
        var size = buf.Length;
        return QueryFullProcessImageNameW(ph, 0, buf, ref size) ? new string(buf, 0, size) : "?";
    }

    // The parent-pid chain of a WebView2 process with the start times (design D5, S16, S21).
    public static string Chain(int pid)
    {
        var parts = new List<string>();
        var cur = pid;
        for (var step = 0; step < 5; step++)
        {
            var ph = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, cur);
            if (ph == IntPtr.Zero) { parts.Add($"{cur}:gone({Marshal.GetLastWin32Error()})"); break; }
            var pbi = new PROCESS_BASIC_INFORMATION();
            NtQueryInformationProcess(ph, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            GetProcessTimes(ph, out var created, out _, out _, out _);
            var exe = Path.GetFileName(Exe(ph));
            CloseHandle(ph);
            parts.Add($"{cur}:{exe}@{DateTime.FromFileTimeUtc(created):HH:mm:ss.fff}");
            if (!exe.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)) break;
            cur = (int)pbi.InheritedFromUniqueProcessId;
        }
        return string.Join("<-", parts);
    }
}
