@echo off
setlocal 

rem dotnet clean
rem dotnet build
rem dotnet test

for %%p in (Sub Pub) do (
	echo Publishing %%p...
	dotnet publish src/PSTT.Remote.%%p\PSTT.Remote.%%p.csproj -c Release -o ./publish
)

if not exist %USERPROFILE%\tools\* (
	echo Cant find tools directory for copy
) else (
	echo Copying published files to tools directory...
	copy publish\* %USERPROFILE%\tools\* /Y
)
