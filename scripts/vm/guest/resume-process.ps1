# Test only: the child resumes a suspended program by hand. Run it as Kid (kid-run.sh).
param([string]$Name = 'TestGame')
Add-Type -Name N -Namespace W -MemberDefinition '[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);'
$p = Get-Process $Name
"NtResumeProcess status: {0}" -f [W.N]::NtResumeProcess($p.Handle) | Out-File C:\Users\Public\resume.txt
