# Third-party notices

This product isn't affiliated with or endorsed by Grinding Gear Games in any way.

## Game data and reference artwork

Path of Exile 2 item artwork, names, descriptions and UI fragments are property
of Grinding Gear Games and their respective rightsholders. They are not offered
under this project's MIT license. The small UI and item crops are development
recognition references; full gameplay captures are not distributed.

- Catalog names, option dictionaries and reference images: https://poe2db.tw/
- Current item art: https://web.poecdn.com/ via https://www.pathofexile.com/api/trade2/data/static
- Per-item source URLs and image mappings: data/catalog.json and data/official-references.json.
- Currency observations: https://poe2scout.com/
- Unique asking prices: https://www.pathofexile.com/trade2 (not completed trades).

## Software dependencies

The self-contained executable includes .NET (MIT), OpenCvSharp (Apache-2.0),
OpenCV (Apache-2.0 and bundled third-party notices), Microsoft.Data.Sqlite and
SQLitePCLRaw (MIT / Apache-2.0 components; SQLite public domain), and Vortice.Windows
(MIT). Transitive packages and pinned versions are recorded in the NuGet lock files.
Full notices supplied by installed packages are in third-party/licenses.

- https://github.com/dotnet/runtime
- https://github.com/shimat/opencvsharp
- https://github.com/opencv/opencv
- https://github.com/dotnet/efcore
- https://github.com/ericsink/SQLitePCL.raw
- https://github.com/amerkoleci/Vortice.Windows

Python tools are separate development dependencies and are not included as a Python
runtime in the executable. See tools/requirements.txt for their package versions.
