To run:
    dotnet restore

USAGE
  dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj -- [url] [volume] [flags]

POSITIONAL ARGUMENTS (order-dependent)

  url        (optional)
             Table-of-contents page to scrape
             Default:
               https://wanderinginn.com/table-of-contents/

  volume     (optional)
             Volume number to build (1-based index)
             Default:
               All volumes

FLAGS (order-independent)

  --format epub|pdf
             Output format
             Default: epub

  --offline
             Disable all network access
             Requires:
               - Cached chapters in /chapters
               - chapters/manifest.json

VALID EXAMPLES

  Default (EPUB, all volumes, online cache-first)
    dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj --

  Single volume
    dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj -- 1

  Explicit EPUB
    dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj -- --format epub

  PDF output
    dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj -- --format pdf

  Offline rebuild (all volumes)
    dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj -- --offline

  Offline rebuild (single volume)
    dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj -- --offline 2

  URL override + volume
    dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj -- https://wanderinginn.com/table-of-contents/ 4

  URL override + PDF + single volume
    dotnet run --project WanderingInnToPdf/WanderingInnToPdf.csproj -- https://wanderinginn.com/table-of-contents/ 4 --format pdf

SUMMARY

  Positional arguments:
    - url
    - volume

  Flags:
    - --format epub|pdf
    - --offline
