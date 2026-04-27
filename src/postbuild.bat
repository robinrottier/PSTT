echo POST BUILD
echo %1 %2 %3 %4
if "%1"=="Release" (
	echo Copying files for Release build...
) else if "%1"=="Debug" (
	echo Nothing to do for Debug build...
) else (
	echo Unknown build configuration: %1
)
