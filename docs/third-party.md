# Pinned dependencies and redistribution

|Dependency|Version|Purpose / license|
|---|---|---|
|.NET SDK|10.0.401|Build tools; Microsoft .NET distribution terms|
|OpenCV|4.14.0|Native image processing; Apache-2.0 plus bundled third-party notices|
|GoogleTest|1.17.0|Native tests; BSD-3-Clause|
|Microsoft.Data.Sqlite|10.0.12|Persistence; MIT|
|SQLitePCLRaw.bundle_e_sqlite3|3.0.5|SQLite native runtime; resolved dependencies in packages.lock.json|
|Microsoft.NET.Test.Sdk|17.14.1|Test execution; Microsoft package license|
|xUnit|2.9.3|Regression tests; Apache-2.0|
|xunit.runner.visualstudio|3.1.4|Test adapter; Apache-2.0|
|MSVC redistributable|Local VC143 runtime|Copied from the licensed Visual Studio redistribution directory; Microsoft terms|

OpenCV installer source: https://github.com/opencv/opencv/releases/download/4.14.0/opencv-4.14.0-windows.exe

OpenCV SHA256: `5F266A8B73BED535962D7E861A6457E32A0DD5F463AD0A7CF8707A135469BE63`.

GoogleTest archive SHA256: `40D4EC942217DCC84A9EBE2A68584ADA7D4A33A8EE958755763278EA1C5E18FF`.

NuGet package content hashes are recorded in committed lock files. Downloads and caches are local to `.tools`. The release includes OpenCV and repository license texts and .NET's runtime notices. Consult the packages' official license files and Microsoft's redistribution list for their complete terms.

FFmpeg is used locally only to encode the application-generated demo frames; it is not a runtime dependency or part of the application package. Its downloadable build is retained under `.tools/ffmpeg` on D:.
