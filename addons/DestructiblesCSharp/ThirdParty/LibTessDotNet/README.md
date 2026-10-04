LibTessDotNet 1.1.15
====================

Vendored from https://github.com/speps/LibTessDotNet/tree/0f7cf440d1793fbec4cd73e09f3c8daf0becf279/LibTessDotNet/Sources. See LICENSE.txt and the copyright notices in each source file.

Only changes: enable DOUBLE in each file, use the Destructibles.Tessellation namespace, and normalize line endings and trailing whitespace. Keeping the source in the addon makes it self-contained; users do not need a NuGet dependency. The cutter uses its GLU sweep tessellator to triangulate concave caps, holes, and nested islands with the even-odd winding rule.
