@echo off
rem Produit dist\Watcher.exe : un seul fichier autonome, runtime .NET embarque.
rem Le RID est passe ici et non dans le .csproj, sinon « dotnet build » deplacerait
rem sa sortie et laisserait un executable perime dans bin\Debug\net9.0-windows\.

setlocal
cd /d "%~dp0"

echo Nettoyage de dist\ ...
if exist dist rmdir /s /q dist

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
if errorlevel 1 (
    echo.
    echo ECHEC de la publication.
    exit /b 1
)

echo.
echo Termine : %~dp0dist\Watcher.exe
dir /b /-c dist
endlocal
