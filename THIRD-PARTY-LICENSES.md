# Third-Party Licenses

This project, UXR.QuestMeshing, is licensed under the Apache License 2.0. See the LICENSE file for the full text of the Apache License 2.0.

The following third-party components are included or adapted in this project. Each is licensed under the MIT License, with the relevant copyright notices and full license texts provided below. Attribution is given in the source code where applicable.

## lasertag (Frustum Volume Initialization and TSDF Volume Population)

**Source**: https://github.com/anaglyphs/lasertag
**Usage**: Adapted for frustum volume point generation in `DepthMesher.InitializeFrustumVolume` and TSDF volume population in kernels of `SurfaceNets.compute` (`Clear`, `UpdateVoxels`).
**License**: MIT License
**Copyright**: Copyright (c) 2024 Julian Triveri & Hazel Roeder

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## naive-surface-nets (Surface Nets Meshing)

**Source**: https://github.com/Q-Minh/naive-surface-nets
**Usage**: Adapted for the Surface Nets algorithm in kernels of `SurfaceNets.compute` (`SurfaceNetsVertexPass` and `SurfaceNetsTrianglePass`).
**License**: MIT License
**Copyright**: Copyright (c) 2020 Quoc-Minh Ton-That

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

*This file was last updated on November 13, 2025.*