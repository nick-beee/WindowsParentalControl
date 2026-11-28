# As Kid: signs out (task 6.3). Output: C:\Users\Public\kid-signout.txt
"$(Get-Date -Format HH:mm:ss.fff) shutdown /l" | Add-Content C:\Users\Public\kid-signout.txt
shutdown /l
