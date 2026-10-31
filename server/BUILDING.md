# Build and run the server

Use the .NET SDK selected by `global.json`. All required source configuration is in
this repository; no private build directory is needed.

```powershell
dotnet build server/Host/Brs.Local.Host.csproj -c Release
dotnet run --project tests/server -c Release
dotnet run --project server/Host -c Release --no-build -- --help
```

Source includes the current 22 rule configurations, bootstrap settings, management
labels, translations and content policy. Their exact identities remain pinned by
`Runtime/runtime-input-manifest.json`. Desktop and Android compose the same gameplay
backend. Original bulk resources, protocol/codec bytes, generated Master templates,
signing keys, owner credentials and account databases are absent.

## Import one resource archive

After building, import the supplied `brs-resources.tar` into a data directory outside
the source checkout, then start the server using that directory:

```powershell
dotnet run --project server/Host -c Release --no-build -- --data-directory $dataDirectory --import-content $resourceTar
dotnet run --project server/Host -c Release --no-build -- --data-directory $dataDirectory --check-config
dotnet run --project server/Host -c Release --no-build -- --data-directory $dataDirectory
```

This standard uncompressed TAR contains a pinned inventory, fixed resource material
and the unchanged supported `content.zip`. Import reads sequentially, including from
a nonseekable Android content URI. Each material's exact name, length and SHA-256
and the original archive's full SHA-256 and entry inventory are verified before
publication. There is no second build or additional private bundle for the user.
The original `content.zip` alone lacks runtime codec, templates and fixed protocol
material and is not this import file.

Import publishes `content/generation-v1` without replacing an existing generation.
A verified older generation can receive matching materials from the same TAR while
retaining its archive and receipt. Reimporting identical material retains existing
files. Failed or canceled imports remove only their own stages; accounts and owner
credentials stay in place. Android asks before removing interrupted package stages.

`--check-config` verifies resources, opens the CDN snapshot, and prepares fixed runtime inputs without opening
the account database or listeners. Without an imported package it returns exit 2
and `setup_required_resource_archive`. After import, source rules and imported
catalog/overlay compose automatically. No private directory argument or build
property is needed. Source configuration is prepared in `runtime-source-v1/`; any older `runtime/`
tree is retained untouched.

The data directory owns `runtime-source-v1/`, `content/`, `accounts.sqlite3` and `owner.key`.
The host generates owner credentials locally. Read `owner.key` locally to log in;
it is not printed. Default loopback ports: game HTTP 18445, management HTTP 18180.
Override with `--game-port` and `--admin-port`. Stop with Ctrl+C. The desktop host
does not configure client routing or trust settings.

## Integration checks

```powershell
dotnet run --project tests/server -c Release -- --content-store $contentStore
```

Here `$contentStore` is the data directory's `content` subdirectory. Tests use
temporary databases and cover all 22 rules, current Expert policy, management icons,
configuration identity, transaction rollback, management authentication/CSRF, and
invalid/canceled TAR import boundaries. They do not claim hardware or game-client
walkthrough validation.

## Developer archive assembly

Ordinary users need only the source build and one supplied TAR. Developers assemble
that TAR from explicitly provided reviewed material and the supported original:

```powershell
python tools/pack-resources.py --materials $reviewedMaterialDirectory --content $originalContentZip --output $newResourceTar
```

The material directory supplies only names pinned by
`Content/resource-package-manifest.json`: `protocol-key.bin`, catalog/local overlay,
Expert binaries/catalog/codec, and management icons. Source rules, bootstrap, labels
and policy are built from this repository and are not additional user import inputs.
The tool checks every small material before writing, refuses an existing output,
and stores the original archive unchanged. Import verifies that large archive while
copying it into the store, avoiding a redundant full hash pass during assembly.
Neither tool searches devices, account databases or another directory implicitly.
