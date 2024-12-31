REM Python Compile the Algorithm with all the files in current directory
Python -m compileall -lq ..\..\

REM Copy to the Lean Algorithm Project
copy ..\..\__pycache__\*.pyc ..\..\..\MyLauncher\bin\Debug
rem copy ..\..\__pycache__\*.pyc ..\..\..\MyLauncher\bin\Release
rem copy ..\..\__pycache__\*.pyc ..\..\..\Tests\bin\Debug >NUL

REM Script intentionally discards errors. This line ensures the exit code is 0.