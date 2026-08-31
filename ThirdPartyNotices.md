# Third-Party Notices

OpenShell includes code derived from the PowerShell project
(https://github.com/PowerShell/PowerShell), used under the MIT license.
Per the reuse policy documented in `docs/ps-ref-reuse-audit.md`, derived
files retain their original copyright headers.

## PowerShell (MIT License)

- `src/OpenShell.Core/Parsing/CharTraits.cs` — ported from the PowerShell
  tokenizer character-trait tables.

MIT License

Copyright (c) Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## SixLabors.ImageSharp

Image preview decoding (IH-009, `ImagePreviewer` / `VideoPreviewer` thumbnail)
uses SixLabors.ImageSharp (https://github.com/SixLabors/ImageSharp), a pure
managed, cross-platform image codec library with no native binaries.

ImageSharp 3.x is distributed under the Six Labors Split License: free
(Apache-2.0-style) for individuals and organizations below the revenue
threshold defined by Six Labors; larger commercial users require a paid
license. See https://sixlabors.com/licenses/ and the package license
metadata on nuget.org for the governing terms.

## NuGet dependencies

Runtime dependencies (SSH.NET, BouncyCastle.Cryptography, Avalonia,
ReactiveUI, Serilog, OpenTelemetry, Tomlyn, Microsoft.Data.Sqlite,
SixLabors.ImageSharp, and Microsoft.Extensions.*) are distributed under
their respective licenses; see each package's license metadata on nuget.org.
