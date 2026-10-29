using UnityEngine;
using UnityEngine.UI;

namespace MORLA.XR
{
    public sealed class WorkspaceIcon : MaskableGraphic
    {
        public enum Kind { Play, Pause, Camera, Microphone, Settings }
        public Kind kind;
        public void SetKind(Kind value) { if (kind == value) return; kind = value; SetVerticesDirty(); }
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (kind == Kind.Play)
            { Line(mesh, new Vector2(-9, -13), new Vector2(12, 0)); Line(mesh, new Vector2(12, 0), new Vector2(-9, 13)); Line(mesh, new Vector2(-9, 13), new Vector2(-9, -13)); }
            else if (kind == Kind.Pause)
            { Line(mesh, new Vector2(-6, -13), new Vector2(-6, 13), 4.5f); Line(mesh, new Vector2(6, -13), new Vector2(6, 13), 4.5f); }
            else if (kind == Kind.Camera)
            {
                Line(mesh, new Vector2(-15, -10), new Vector2(4, -10)); Line(mesh, new Vector2(4, -10), new Vector2(4, 10));
                Line(mesh, new Vector2(4, 10), new Vector2(-15, 10)); Line(mesh, new Vector2(-15, 10), new Vector2(-15, -10));
                Line(mesh, new Vector2(4, -5), new Vector2(14, -11)); Line(mesh, new Vector2(14, -11), new Vector2(14, 11)); Line(mesh, new Vector2(14, 11), new Vector2(4, 5));
            }
            else if (kind == Kind.Microphone)
            {
                Arc(mesh, new Vector2(0, 8), 6, 0, 180); Arc(mesh, new Vector2(0, -2), 6, 180, 360);
                Line(mesh, new Vector2(-6, -2), new Vector2(-6, 8)); Line(mesh, new Vector2(6, -2), new Vector2(6, 8));
                Arc(mesh, Vector2.zero, 12, 180, 360); Line(mesh, new Vector2(-12, 0), new Vector2(-12, 3));
                Line(mesh, new Vector2(12, 0), new Vector2(12, 3)); Line(mesh, new Vector2(0, -12), new Vector2(0, -17));
                Line(mesh, new Vector2(-7, -17), new Vector2(7, -17));
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    float y = 11 - i * 11, x = i == 1 ? 7 : -6;
                    Line(mesh, new Vector2(-16, y), new Vector2(16, y));
                    Arc(mesh, new Vector2(x, y), 4, 0, 360);
                }
            }
        }
        private void Arc(VertexHelper mesh, Vector2 center, float radius, float from, float to)
        {
            for (int i = 0; i < 16; i++)
            {
                float a = Mathf.Lerp(from, to, i / 16f) * Mathf.Deg2Rad, b = Mathf.Lerp(from, to, (i + 1) / 16f) * Mathf.Deg2Rad;
                Line(mesh, center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, center + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * radius);
            }
        }
        private void Line(VertexHelper mesh, Vector2 from, Vector2 to, float thickness = 2.2f)
        {
            Vector2 direction = (to - from).normalized; Vector2 normal = new Vector2(-direction.y, direction.x) * thickness * 0.5f;
            int start = mesh.currentVertCount;
            mesh.AddVert(from - normal, color, Vector2.zero); mesh.AddVert(from + normal, color, Vector2.zero);
            mesh.AddVert(to + normal, color, Vector2.zero); mesh.AddVert(to - normal, color, Vector2.zero);
            mesh.AddTriangle(start, start + 1, start + 2); mesh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
