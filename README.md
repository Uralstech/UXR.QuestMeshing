# UXR.QuestMeshing

Real-time environment mesh, navmesh, and collision generation for Unity using the Meta Quest Depth API.

[![openupm](https://img.shields.io/npm/v/com.uralstech.uxr.questmeshing?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.uralstech.uxr.questmeshing/)
[![openupm](https://img.shields.io/badge/dynamic/json?color=brightgreen&label=downloads&query=%24.downloads&suffix=%2Fmonth&url=https%3A%2F%2Fpackage.openupm.com%2Fdownloads%2Fpoint%2Flast-month%2Fcom.uralstech.uxr.questmeshing)](https://openupm.com/packages/com.uralstech.uxr.questmeshing/)

## Installation

This package was designed for Unity 6.0 and above. Built and tested in Unity 6.4.

### OpenUPM

1. Open project settings
2. Select `Package Manager`
3. Add the OpenUPM package registry:
    - Name: `OpenUPM`
    - URL: `https://package.openupm.com`
    - Scope(s)
        - `com.uralstech`
4. Open the Unity Package Manager window (`Window` -> `Package Manager`)
5. Change the registry from `Unity` to `My Registries`
6. Add the `UXR.QuestMeshing` package

### Unity Package Manager

1. Open the Unity Package Manager window (`Window` -> `Package Manager`)
2. Select the `+` icon and `Add package from git URL...`
3. Paste the UPM branch URL and press enter:
    - `https://github.com/Uralstech/UXR.QuestMeshing.git#upm`
4. Check the instructions for [`Utils.Singleton`](https://uralstech.github.io/Utils.Singleton) to install the dependency

### GitHub Clone

1. Clone or download the repository from the desired branch (master, preview/unstable)
2. Drag the package folder `UXR.QuestMeshing/UXR.QuestMeshing/Packages/com.uralstech.uxr.questmeshing` into your Unity project's `Packages` folder
3. Check the instructions for [`Utils.Singleton`](https://uralstech.github.io/Utils.Singleton) to install the dependency

## Preview Versions

Do not use preview versions (i.e. versions that end with "-preview") for production use as they are unstable and untested.

## Documentation

See <https://uralstech.github.io/UXR.QuestMeshing/DocSource/QuickStart.html> or `APIReferenceManual.pdf` and `Documentation.pdf` in the package documentation for the reference manual and tutorial.
