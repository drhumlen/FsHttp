
dotnet tool restore
dotnet publish ./src/FsHttp.sln -c Release -f netstandard2.0

if ($args[0] -eq $null) {
	$mode = "build"
} else {
	$mode = "watch"
}

dotnet fsdocs `
	$mode `
	--clean `
	--sourcefolder ./src `
	--input ./src/docs `
	--output ./docs `
	--properties Configuration=Release `
	--sourcerepo https://github.com/fsprojects/FsHttp/blob/master/src `
	--parameters root ./
