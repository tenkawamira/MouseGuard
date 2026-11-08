# Third-Party Notices

MouseGuard's original source code is licensed under GNU GPL version 3 only
(`GPL-3.0-only`). That license does not relicense the third-party components
below.

## .NET 8

- Component: .NET runtime and base class libraries
- License: MIT
- Copyright: .NET Foundation and contributors
- Source: https://github.com/dotnet/runtime
- License text: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

The self-contained portable build includes the .NET runtime under the MIT
license. Its copyright and permission notice must be preserved.

## Microsoft Windows App SDK 2.6.5 / WinUI 3

- Component: `Microsoft.WindowsAppSDK.*` NuGet packages and binplaced
  Windows App SDK/WinUI runtime files
- License: Microsoft Software License Terms for the Microsoft Windows App SDK
- Package license: the `license.txt` supplied with each NuGet package
- Product page: https://learn.microsoft.com/windows/apps/windows-app-sdk/

The Windows App SDK is **not** licensed under GPL-3.0 and is not relicensed by
this repository. The self-contained release package includes Windows App SDK
redistributable files under Microsoft's terms. In particular, section 3 of the
Microsoft license governs distribution of "Distributable Code" and its
requirements/restrictions. Do not remove Microsoft notices from those files.

## Windows SDK reference assemblies

- Component: `Microsoft.Windows.SDK.NET.Ref`
- License: Microsoft Software License Terms
- These reference assemblies are used at build time and are not a substitute
  for the Windows App SDK license.

## Fonts and system icons

The UI uses Segoe UI / Segoe Fluent Icons supplied by Windows. No font files
are redistributed by this repository.

## Licensing note

GPL-3.0 applies to the original MouseGuard source in this repository. The
repository does not vendor the Microsoft binaries; they are restored by NuGet
at build time. Distributing a self-contained binary that combines GPL-covered
code with Microsoft's Windows App SDK is a combined-distribution question.
Review Microsoft's current license terms and, for a commercial or
high-stakes distribution, obtain legal advice before publishing such a
binary.

This notice is informational and is not legal advice.
