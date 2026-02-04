; Electron.NET DotNet-First NSIS Custom Install Script
; This script reorganizes the directory structure after installation
; to make .NET executable the main entry point

!macro customInstall
  ; Create electron subdirectory
  CreateDirectory "$INSTDIR\electron"
  CreateDirectory "$INSTDIR\electron\resources"
  CreateDirectory "$INSTDIR\electron\locales"

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

  ; Move resources directory contents
  CopyFiles /SILENT "$INSTDIR\resources\*.*" "$INSTDIR\electron\resources"
  RMDir /r "$INSTDIR\resources"

  ; Move locales directory
  CopyFiles /SILENT "$INSTDIR\locales\*.*" "$INSTDIR\electron\locales"
  RMDir /r "$INSTDIR\locales"

  ; Move .NET files from dotnet subdirectory to root
  CopyFiles /SILENT "$INSTDIR\dotnet\*.*" "$INSTDIR"

  ; Copy subdirectories from dotnet (Resources, dist, etc.)
  IfFileExists "$INSTDIR\dotnet\Resources\*.*" 0 +3
    CreateDirectory "$INSTDIR\Resources"
    CopyFiles /SILENT "$INSTDIR\dotnet\Resources\*.*" "$INSTDIR\Resources"

  IfFileExists "$INSTDIR\dotnet\dist\*.*" 0 +3
    CreateDirectory "$INSTDIR\dist"
    CopyFiles /SILENT "$INSTDIR\dotnet\dist\*.*" "$INSTDIR\dist"

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
