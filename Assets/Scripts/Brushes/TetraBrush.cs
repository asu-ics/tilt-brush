// Copyright 2020 The Tilt Brush Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#pragma warning disable 219

using UnityEngine;

namespace TiltBrush {

// TODO: Could be slightly more vtx-efficient with a non-textured tube
// (no need to duplicate verts along seam)
// TODO: remove use of nRight, nSurface
class TetraBrush : GeometryBrush {
  const float M2U = App.METERS_TO_UNITS;
  const float U2M = App.UNITS_TO_METERS;

  const float TWOPI = 2 * Mathf.PI;

  const float kMinimumMove = 5e-4f * M2U;
  const float kCapAspect = .8f;
  const ushort kVertsInClosedCircle = 4;
  const ushort kVertsInCap = kVertsInClosedCircle-1;

  const float kBreakAngleScalar = 3.0f;
  const float kSolidMinLengthMeters = 0.002f;
  const float kSolidAspectRatio = 0.2f;

  /// Positive multiplier; 1.0 is standard, higher is more sensitive.
  [SerializeField] float m_BreakAngleMultiplier = 2;

  /// Amount of texture to chop off left and right edges, because
  /// interesting textures have ragged edges which don't work well when
  /// wrapped around tubes.
  [SerializeField] float m_TextureEdgeChop = 0.0f;

  protected enum UVStyle {
    Distance,
    Unitized
  };

  [SerializeField]
  protected UVStyle m_uvStyle = UVStyle.Distance;

  public TetraBrush() : this(true) {}

  public TetraBrush(bool bCanBatch)
    : base(bCanBatch: bCanBatch,
           upperBoundVertsPerKnot: kVertsInClosedCircle * 2,
           bDoubleSided: false) {
    // Start and end of circle are coincident, and need at least one more point.
    Debug.Assert(kVertsInClosedCircle > 2);
  }

  //
  // GeometryBrush API
  //

  protected override void InitBrush(BrushDescriptor desc, TrTransform localPointerXf) {
    base.InitBrush(desc, localPointerXf);
    m_geometry.Layout = GetVertexLayout(desc);
  }

  override public GeometryPool.VertexLayout GetVertexLayout(BrushDescriptor desc) {
    return new GeometryPool.VertexLayout {
      uv0Size = 2,
      uv1Size = 0,
      bUseNormals = true,
      bUseColors = true,
      bUseTangents = true,
    };
  }

  override public float GetSpawnInterval(float pressure01) {
    return kSolidMinLengthMeters * App.METERS_TO_UNITS +
      (PressuredSize(pressure01) * kSolidAspectRatio);
  }

  override protected void ControlPointsChanged(int iKnot0) {
    // Updating a control point affects geometry generated by previous knot
    // (if there is any). The HasGeometry check is not a micro-optimization:
    // it also keeps us from backing up past knot 0.
    int start = (m_knots[iKnot0 - 1].HasGeometry) ? iKnot0 - 1 : iKnot0;

    // Frames knots, determines how much geometry each knot should get
    OnChanged_FrameKnots(start);
    OnChanged_MakeGeometry(start);
    ResizeGeometry();
  }

  // This approximates parallel transport.
  static Quaternion ComputeMinimalRotationFrame(
    Vector3 nTangent, Quaternion qPrevFrame) {
    Vector3 nPrevTangent = qPrevFrame * Vector3.forward;
    Quaternion minimal = Quaternion.FromToRotation(nPrevTangent, nTangent);
    return minimal * qPrevFrame;
  }

  // Fills in any knot data needed for geometry generation.
  // - fill in length, nRight, nSurface, iVert, iTri
  // - calculate strip-break points
  void OnChanged_FrameKnots(int iKnot0) {
    Knot prev = m_knots[iKnot0-1];
    for (int iKnot = iKnot0; iKnot < m_knots.Count; ++iKnot) {
      Knot cur = m_knots[iKnot];

      bool shouldBreak = false;

      Vector3 vMove = cur.smoothedPos - prev.smoothedPos;
      cur.length = vMove.magnitude;

      if (cur.length < kMinimumMove) {
        shouldBreak = true;
      } else {
        Vector3 nTangent = vMove / cur.length;
        if (prev.HasGeometry) {
          cur.qFrame = ComputeMinimalRotationFrame(nTangent, prev.qFrame);
        } else {
          Vector3 nRight, nUp;
          // No previous orientation; compute a reasonable starting point
          ComputeSurfaceFrameNew(Vector3.zero, nTangent, cur.point.m_Orient, out nRight, out nUp);
          cur.qFrame = Quaternion.LookRotation(nTangent, nUp);
        }

        // More break checking; replicates previous logic
        // TODO: decompose into twist and swing; use different constraints
        // http://www.euclideanspace.com/maths/geometry/rotations/for/decomposition/
        if (prev.HasGeometry && !m_PreviewMode) {
          float fWidthHeightRatio = cur.length / PressuredSize(cur.smoothedPressure);
          float fBreakAngle = Mathf.Atan(fWidthHeightRatio) * Mathf.Rad2Deg * m_BreakAngleMultiplier;
          float angle = Quaternion.Angle(prev.qFrame, cur.qFrame);
          if (angle > fBreakAngle) {
            shouldBreak = true;
          }
        }
      }

      if (shouldBreak) {
        cur.qFrame = new Quaternion(0,0,0,0);
        cur.nRight = cur.nSurface = Vector3.zero;
      } else {
        cur.nRight = cur.qFrame * Vector3.right;
        cur.nSurface = cur.qFrame * Vector3.up;
      }

      // Just mark whether or not the strip is broken
      // tri/vert allocation will happen next pass
      cur.nTri = cur.nVert = (ushort)(shouldBreak ? 0 : 1);
      m_knots[iKnot] = cur;
      prev = cur;
    }
  }

  // Textures are laid out so u goes along the strip,
  // and v goes across the strip (from left to right)
  void OnChanged_MakeGeometry(int iKnot0) {
    // Invariant: there is a previous knot.
    Knot prev = m_knots[iKnot0-1];
    for (int iKnot = iKnot0; iKnot < m_knots.Count; ++iKnot) {
      // Invariant: all of prev's geometry (if any) is correct and up-to-date.
      // Thus, there is no need to modify anything shared with prev.
      Knot cur = m_knots[iKnot];

      cur.iTri = prev.iTri + prev.nTri;
      cur.iVert = (ushort)(prev.iVert + prev.nVert);

      // Verts are: back cap, back circle, front circle, front cap
      // Back circle is shared with previous knot

      if (cur.HasGeometry) {
        cur.nVert = cur.nTri = 0;

        Vector3 rt  = cur.qFrame * Vector3.right;
        Vector3 up  = cur.qFrame * Vector3.up;
        Vector3 fwd = cur.qFrame * Vector3.forward;

        // Verts, back half

        float u0, v0, v1;

        // Verts, back half
        float random01 = m_rng.In01(cur.iVert - 1);
        if (m_uvStyle == UVStyle.Unitized) {
          u0 = 0;
        } else {
          u0 = random01;
        }

        int numV = m_Desc.m_TextureAtlasV;
        int iAtlas = (int) (random01 * 3331) % numV;
        v0 = (iAtlas   + m_TextureEdgeChop) / (float) numV;
        v1 = (iAtlas+1 - m_TextureEdgeChop) / (float) numV;

        float prevSize = PressuredSize(prev.smoothedPressure);
        float prevRadius = prevSize / 2;
        float prevCircumference = TWOPI * prevRadius;
        float prevURate = m_Desc.m_TileRate / prevCircumference;

        MakeClosedCircle(ref cur, prev.smoothedPos, prevRadius,
            kVertsInClosedCircle, up, rt, fwd, u0, v0, v1);


        // Verts, front point
        {
          float size = PressuredSize(cur.smoothedPressure);
          float radius = size / 2;
          float circumference = TWOPI * radius;
          float uRate = m_Desc.m_TileRate / circumference;

          Vector2 uv = Vector3.zero;
          AppendVert(ref cur, cur.smoothedPos, fwd, m_Color, fwd, uv);
        }

        // Tris
        int BC =  0;
        int FC = BC + kVertsInClosedCircle;  // vert index of front circle

        // Connect back circle to front point
        for (int i = 0; i < kVertsInClosedCircle-1; ++i) {
          int ii = (i+1);
          AppendTri(ref cur, BC+i, FC, BC+ii);
        }

        // Back of tetrahedron
        AppendTri(ref cur, BC+0, BC+1, BC+2);
        AppendTri(ref cur, BC+2, BC+3, BC+0);
      }

      m_knots[iKnot] = cur;
      prev = cur;
    }
  }

  void MakeCapVerts(
      ref Knot k, int num,
      Vector3 tip, Vector3 circleCenter, float radius,
      float u0, float v0, float v1, float uRate,
      Vector3 up, Vector3 rt, Vector3 fwd)
  {
    // Length of diagonal between circle and tip
    float diagonal = ((circleCenter + up * radius) - tip).magnitude;
    float u = u0 + uRate * diagonal;

    Vector3 normal = Mathf.Sign(Vector3.Dot(tip - circleCenter, fwd)) * fwd;
    for (int i = 0; i < num; ++i) {
      // Endcap vert n tangent points halfway between circle verts n and (n+1)
      float t = (i + .5f) / num;
      float theta = TWOPI * t;
      Vector3 tan = -Mathf.Cos(theta) * up + -Mathf.Sin(theta) * rt;
      Vector2 uv = new Vector2(u, Mathf.Lerp(v0, v1, t));
      AppendVert(ref k, tip, normal, m_Color, tan, uv);
    }
  }

  void MakeClosedCircle(
    ref Knot k, Vector3 center, float radius, int num,
    Vector3 up, Vector3 rt, Vector3 fwd,
    float u, float v0, float v1) {
    // When facing down the tangent, circle verts should go clockwise
    // We'd like the seam to be on the bottom
    up *= radius;
    rt *= radius;
    for (int i = 0; i < num; ++i) {
      float t = (float)i / (num-1);
      // Ensure that the first and last verts are exactly coincident
      float theta = (t == 1) ? 0 : TWOPI * t;

      Vector2 uv;
      if (m_uvStyle == UVStyle.Unitized) {
		uv = new Vector2(u,i);
	  }
	  else {
		uv = new Vector2(u, Mathf.Lerp(v0, v1, t));
	  }
      Vector3 off = -Mathf.Cos(theta) * up + -Mathf.Sin(theta) * rt;
      AppendVert(ref k, center + off, off.normalized, m_Color, fwd, uv);
    }
  }

  /// Resizes arrays if necessary, appends data, mutates knot's vtx count. The
  /// incoming normal n should be normalized.
  void AppendVert(ref Knot k, Vector3 v, Vector3 n, Color32 c,
                 Vector3 tan, Vector2 uv) {
    int i = k.iVert + k.nVert++;
    Vector4 tan4 = tan;
    tan4.w = 1;
    if (i == m_geometry.m_Vertices.Count) {
      m_geometry.m_Vertices .Add(v);
      m_geometry.m_Normals  .Add(n);
      m_geometry.m_Colors   .Add(c);
      m_geometry.m_Tangents .Add(tan4);
      m_geometry.m_Texcoord0.v2.Add(uv);
    } else {
      m_geometry.m_Vertices[i] = v;
      m_geometry.m_Normals[i]  = n;
      m_geometry.m_Colors[i]   = c;
      m_geometry.m_Tangents[i] = tan4;
      m_geometry.m_Texcoord0.v2[i] = uv;
    }
  }

  void AppendTri(ref Knot k, int t0, int t1, int t2) {
    int i = (k.iTri + k.nTri++) * 3;
    if (i == m_geometry.m_Tris.Count) {
      m_geometry.m_Tris.Add(k.iVert + t0);
      m_geometry.m_Tris.Add(k.iVert + t1);
      m_geometry.m_Tris.Add(k.iVert + t2);
    } else {
      m_geometry.m_Tris[i + 0] = k.iVert + t0;
      m_geometry.m_Tris[i + 1] = k.iVert + t1;
      m_geometry.m_Tris[i + 2] = k.iVert + t2;
    }
  }

  bool IsPenultimate(int iKnot) {
    return (iKnot+1 == m_knots.Count || !m_knots[iKnot+1].HasGeometry);
  }
}
}  // namespace TiltBrush
