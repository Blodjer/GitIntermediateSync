@echo off
Powershell -Command "& { $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true); $path = $key.GetValue('Path',$null,'DoNotExpandEnvironmentNames'); $key.SetValue('Path', $path + ';%%OneDriveConsumer%%\!sync\git;', 'ExpandString'); $key.Dispose(); }"
echo.
echo Command registered. Please restart the computer to complete the setup!
echo.
pause