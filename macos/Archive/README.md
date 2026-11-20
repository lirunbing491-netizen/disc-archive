# macOS raw archive backend

`../Core/MacSevenZip.cs` replaces the Windows COM reader and interop files. It
retains their `DiscArchive.SevenZipReader` / `ArchiveItem` API and field meanings.
Include this file instead of `src/SevenZipReader.cs` and `src/SevenZipInterop.cs`.

The C++ bridge loads the bundled upstream Unix `7z.so` through an absolute path.
Both binaries belong in the architecture-specific native resource directory;
set `DISCARCHIVE_NATIVE_DIR` to that absolute directory. No installed 7-Zip,
driver, filesystem mount, shell listing, or filename-based extraction is used.

Build on macOS (requires the normal Xcode command line build tools):

```sh
bash macos/Archive/build-native.sh arm64 /absolute/output/arm64 /absolute/7zip-source
bash macos/Archive/build-native.sh x64 /absolute/output/x64 /absolute/7zip-source
bash macos/Archive/run-tests.sh /absolute/output/arm64
```

The optional third argument is an extracted `7z2606-src.7z` source root. Without
it, the script extracts the repository's archive with `tar`. CI need not install
p7zip when its `tar` supports 7z; otherwise extract with an existing 7zz and pass
the source root. The script copies source into a fresh temporary directory and
uses upstream `CPP/7zip/Bundles/Format7zF/makefile.gcc`. It emits `7z.so` and
`libdiscarchive7z.dylib`. It also supports matching-architecture Linux builds for
isolated ABI tests, producing `libdiscarchive7z.so` instead.

The parent stream must already be a bounded partition stream. Its caller retains
ownership. Every archive and item operation is serialized. Archive items retain
upstream indices and raw parent indices; names are never used as lookup keys.
This preserves case-distinct entries, duplicate names, resource-fork entries,
and literal path punctuation. Links remain metadata and cannot be followed.

Uncompressed entries use `IInArchiveGetStream` only when the returned object
actually provides `IInStream`. Each outstanding stream saves/restores its own
source cursor. Reading or seeking one item therefore cannot silently change
another stream's cursor. Seeking does not read the preceding bytes. Source
callback errors and premature EOF propagate as errors. A failed item stream
cannot subsequently return apparently successful data.

Compressed, APFS, or nonseekable items are decoded by exact archive item index
to one delete-on-close temporary file. The bridge requires the extraction
callback to have requested that index, operation result zero, and exactly the
declared decoded length. Decoded overflow, unsupported methods, corrupt data,
missing callback results, resource-fork failure, and source I/O errors fail
explicitly. APFS always uses decoding because upstream direct streams can expose
stored decmpfs bytes. APFS remains experimental; hardlink support remains
explicitly unvalidated (`HardLinkSupportKnown == false`).

Tests use small synthetic ISO, HFS+, HFSX and compressed HFS fixtures derived
from the Windows regression suite, including resource forks, duplicate names,
case-distinct names, raw parent relations, independent seek cursors, injected
source faults and unsupported/corrupt compression. On macOS the test runner
also authors Joliet and UDF fixtures from a single `hello` test file using
`hdiutil makehybrid`; it never attaches or mounts these images. These tests do
not validate physical-disc hardware or APFS images.

At initial implementation the .NET 8 test project compiled with zero errors and
warnings on Windows. Native builds and backend runtime tests were not executable
there: the available WSL image had no C/C++ compiler or make. A macOS CI run is
required before claiming backend validation or shipping the native binaries.
