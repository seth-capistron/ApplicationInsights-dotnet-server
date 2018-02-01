

Write-Host "Cleaning up iisexpress"
Get-Process -Name vstest.console | Stop-Process
Write-Host "Cleaning up iisexpress completed"


