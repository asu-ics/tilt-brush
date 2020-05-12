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

Shader "Custom/PadTimer" {
  Properties {
    _Color ("Main Color", Color) = (1,1,1,1)
    _BaseDiffuseColor ("Base Diffuse Color", Color) = (.2,.2,.2,1)
    _MainTex ("Texture", 2D) = "white" {}
    _EmissionColor ("Emission Color", Color) = (1,1,1,1)
    _Shininess("Smoothness", Range(0.01, 1)) = 0.013
    _Ratio ("Scroll Ratio", Float) = 1
  }
  SubShader {
    Tags{ "RenderType" = "Opaque" }
    LOD 100

    CGPROGRAM
    #pragma target 3.0
    #pragma surface surf Standard nofog

    sampler2D _MainTex;
    fixed4 _Color;
    fixed4 _BaseDiffuseColor;
    fixed4 _EmissionColor;
    float _Ratio;
    half _Shininess;

    struct Input {
      float2 uv_MainTex;
      float2 uv_TimerTex;
    };

    void surf (Input IN, inout SurfaceOutputStandard o) {
        float adjustedRatio = 0.5 - _Ratio;
      fixed4 c = tex2D(_MainTex, IN.uv_MainTex);
      float angle = atan2(IN.uv_MainTex.x - 0.5, -IN.uv_MainTex.y + 0.5);
      angle /= 3.14159 * 2.0;

      if (angle > adjustedRatio) c = 1 - c;

      c *= _Color + _EmissionColor;
      o.Albedo = c + _BaseDiffuseColor;
      o.Emission = c * normalize(_EmissionColor);
      o.Alpha = 1;
      o.Smoothness = _Shininess;
    }
    ENDCG
  }
  FallBack "Diffuse"
}

