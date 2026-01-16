# Nin-Online-Explorer

Basic program for Nin Online MMORPG, you can view .nin files, export & import them in real time.

The program has a bunch of basic functions like selecting a folder and showing the entire tree of subfolders and .nin files.
You can view in real time the .nin files, and you can export them (one by one or even multiple files).
For import, the program haves 2 ways:
  - Import: Classic 1:1 import, you select a .nin file to be replaced and import a .png that you want in that place.
  - Batch Replace: This one is a bit tricky, you can import in batch but you must select a folder, not the files, and the files must have the same name as the .nin ones.


## Build

To generate a standalone, single-file executable for your specific architecture, use the following commands:

### Windows x64 (64-bit)
`dotnet publish -c Release -r win-x64`

### Windows x86 (32-bit)
`dotnet publish -c Release -r win-x86`

The output will be located in:  
`\bin\Release\net8.0-windows\[runtime]\publish\`
