# As Kid: runs the ChannelAttack test client (tasks 5.3 and 6.4). Publish it first as nick:
#   dotnet publish scripts\vm\tools\ChannelAttack -c Release -o C:\Users\Public\ChannelAttack
# The output goes to C:\Users\Public\channel-attack.log.
& 'C:\Users\Public\ChannelAttack\ChannelAttack.exe' @args
