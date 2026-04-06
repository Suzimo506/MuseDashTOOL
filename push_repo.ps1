 = Join-Path C:\Users\19758\AppData\Local\Temp "NewCollectionAlbums_Push"
if (Test-Path ) { Remove-Item -Recurse -Force  }
New-Item -ItemType Directory -Force 
Set-Location 
git clone https://github.com/KuoKing506/NewCollectionAlbums.git
Set-Location NewCollectionAlbums
Copy-Item "d:\MuseDashModTool\MuseDashTOOL\SongRepository\Collection_index.json" -Destination .
New-Item -ItemType Directory -Force "宫守文学曲包"
Copy-Item -Recurse "d:\MuseDashModTool\MuseDashTOOL\SongRepository\宫守文学曲包\covers" -Destination "宫守文学曲包\covers"
Copy-Item -Recurse "d:\MuseDashModTool\MuseDashTOOL\SongRepository\宫守文学曲包\demos" -Destination "宫守文学曲包\demos"
git add .
git commit -m "Add collection index and 宫守文学曲包 media (covers/demos)"
git push
