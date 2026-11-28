# As Kid: ends the Session Agent as Task Manager does (task 6.4, "Child ends the agent").
# Output: C:\Users\Public\kid-kill-agent.txt
Get-Process ParentalControl.Agent -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    "$(Get-Date -Format HH:mm:ss.fff) ended agent pid $($_.Id)" | Add-Content C:\Users\Public\kid-kill-agent.txt
}
