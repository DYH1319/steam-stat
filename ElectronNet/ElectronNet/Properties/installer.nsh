; Electron.NET DotNet-First NSIS Custom Install Script
; This script reorganizes the directory structure after installation
; to make .NET executable the main entry point

!macro customInit
  ${if} ${isUpdated}
    nsExec::ExecToLog `"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-CimInstance -ClassName Win32_Process | Where-Object { $$_.ExecutablePath -ieq '$INSTDIR\${APP_EXECUTABLE_FILENAME}' } | ForEach-Object { Stop-Process -Id $$_.ProcessId -Force -PassThru -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue }"`
    Sleep 500
  ${endif}
!macroend

!macro customInstall
  ; Create electron subdirectory (do NOT pre-create resources/locales, they will be moved in whole)
  CreateDirectory "$INSTDIR\electron"

  ; Move Electron runtime files to electron subdirectory
  ; Main Electron executable (rename to avoid conflict with .NET exe)
  Rename "$INSTDIR\${APP_EXECUTABLE_FILENAME}" "$INSTDIR\electron\${APP_EXECUTABLE_FILENAME}"

  ; Chromium resources
  Rename "$INSTDIR\chrome_100_percent.pak" "$INSTDIR\electron\chrome_100_percent.pak"
  Rename "$INSTDIR\chrome_200_percent.pak" "$INSTDIR\electron\chrome_200_percent.pak"
  Rename "$INSTDIR\resources.pak" "$INSTDIR\electron\resources.pak"
  Rename "$INSTDIR\icudtl.dat" "$INSTDIR\electron\icudtl.dat"

  ; V8 snapshots
  Rename "$INSTDIR\snapshot_blob.bin" "$INSTDIR\electron\snapshot_blob.bin"
  Rename "$INSTDIR\v8_context_snapshot.bin" "$INSTDIR\electron\v8_context_snapshot.bin"

  ; DLLs
  Rename "$INSTDIR\d3dcompiler_47.dll" "$INSTDIR\electron\d3dcompiler_47.dll"
  Rename "$INSTDIR\ffmpeg.dll" "$INSTDIR\electron\ffmpeg.dll"
  Rename "$INSTDIR\libEGL.dll" "$INSTDIR\electron\libEGL.dll"
  Rename "$INSTDIR\libGLESv2.dll" "$INSTDIR\electron\libGLESv2.dll"
  Rename "$INSTDIR\vk_swiftshader.dll" "$INSTDIR\electron\vk_swiftshader.dll"
  Rename "$INSTDIR\vulkan-1.dll" "$INSTDIR\electron\vulkan-1.dll"

  ; Other files
  Rename "$INSTDIR\LICENSE" "$INSTDIR\electron\LICENSE"
  Rename "$INSTDIR\LICENSE.electron.txt" "$INSTDIR\electron\LICENSE.electron.txt"
  Rename "$INSTDIR\LICENSES.chromium.html" "$INSTDIR\electron\LICENSES.chromium.html"
  Rename "$INSTDIR\version" "$INSTDIR\electron\version"
  Rename "$INSTDIR\vk_swiftshader_icd.json" "$INSTDIR\electron\vk_swiftshader_icd.json"

  ; Move resources and locales directories into electron/ as whole (preserves subdirectories)
  IfFileExists "$INSTDIR\resources\*.*" 0 +2
    Rename "$INSTDIR\resources" "$INSTDIR\electron\resources"

  IfFileExists "$INSTDIR\locales\*.*" 0 +2
    Rename "$INSTDIR\locales" "$INSTDIR\electron\locales"

  ; Copy .NET files from dotnet subdirectory to root
  CopyFiles /SILENT "$INSTDIR\dotnet\*.*" "$INSTDIR"

  ; Move .NET subdirectories from dotnet to root as whole (preserves dist/assets, Resources, etc.)
  IfFileExists "$INSTDIR\dotnet\Resources\*.*" 0 +2
    Rename "$INSTDIR\dotnet\Resources" "$INSTDIR\Resources"

  IfFileExists "$INSTDIR\dotnet\dist\*.*" 0 +2
    Rename "$INSTDIR\dotnet\dist" "$INSTDIR\dist"

  ; Remove dotnet subdirectory
  RMDir /r "$INSTDIR\dotnet"

  ; Update shortcuts to point to .NET executable
  ; The shortcuts are created after customInstall, so we need to handle this differently
  ; Actually, since we renamed the Electron exe and kept the same name for .NET exe,
  ; the shortcuts will automatically point to the .NET exe in $INSTDIR
!macroend

!macro customUnInstall
  ; Clean up electron subdirectory
  RMDir /r "$INSTDIR\electron"
  RMDir /r "$INSTDIR\Resources"
  RMDir /r "$INSTDIR\dist"
!macroend
