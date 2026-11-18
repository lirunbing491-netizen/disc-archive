# Public tests

Run from a local Windows 10/11 desktop checkout:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test.ps1
```

The runner uses Windows PowerShell 5.1 and the .NET Framework C# compiler shipped with Windows. It compiles the public `src/*.cs` files into temporary test executables. No package restore, cloud credentials, optical drive or camera is required. An interactive desktop is needed for WinForms tests; their windows and notifications are positioned off screen.

All image fixtures, TIFF/EXIF metadata, files and catalog records are generated in fresh temporary directories. Project names and dates are fictional. Existing user photos, Camera Roll, personal catalogs and real discs are never used. Generated artifacts remain in the temporary directories printed by the runner; some individual tests remove their own fixtures when finished.

The workflow suite replaces optical hardware and the OCR worker response with deterministic local fixtures. Its assertions cover the real UI event loop, registration, naming, copying, retry, interruption recovery, and reports. `VisionTests.ps1` and `OcrTests.ps1` separately test actual offline Windows OCR against generated images, corrupt/missing image handling and a stalled-worker timeout. English and simplified Chinese recognition assertions explicitly report `SKIP` when the matching installed recognizer is unavailable; no language pack is downloaded. A skipped recognition assertion does not establish OCR quality for that language.

The endpoint suite uses an in-process HTTP fixture bound only to `127.0.0.1` on an operating-system-assigned port. It needs no administrator URL reservation, makes no external request and uses only dummy keys. External example URLs appear only in URL-validation assertions.

Some shipped UI constructors enumerate camera names read-only. They never start camera preview, capture frames or access stored photos. To also print read-only hardware enumeration, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test.ps1 -EnumerateHardware
```

The optical suite checks synthetic native protocol responses and rejection of non-optical paths. It never acquires a real optical device, locks a tray, ejects/loads media or writes to a disc. The camera suite primarily tests generated pixel buffers and image file handling. Its optional `--synthetic-graph` executable argument uses only a generated AVI and does not open a camera.

Passing these suites validates the generated fixtures on the host where they ran. It does not establish behavior on damaged physical media, live camera image quality, cloud-model performance or every Windows/language-pack combination.