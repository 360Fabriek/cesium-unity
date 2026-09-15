#!/usr/bin/env python3
"""Compile the HLSL scalar projection as C++ floats, then compare to double geodesy.

This exercises the checked-in projection formula, NOT Unity shader compilation.
Requires Python 3, NumPy, and a C++ compiler (CXX or c++).
"""
import json
import math
import os
import re
from pathlib import Path
import subprocess
import tempfile
import numpy as np

ROOT = Path(__file__).resolve().parents[1]

def run():
    hlsl = (ROOT / 'Source/Runtime/Resources/CesiumInstancedRaster.hlsl').read_text()
    body = hlsl[hlsl.index('float3 CesiumInstanceAngularDelta'):hlsl.index('\nvoid CesiumRasterOverlay_float')]
    body = re.sub(r'(?<![\w.])(\d+\.\d*(?:[eE][+-]?\d+)?|\d+[eE][+-]?\d+)(?![\w.])',
                  r'\1f', body.replace('[unroll]', ''))
    prefix = '''#include <cmath>
#include <algorithm>
#include <cstdio>
using std::sqrt; using std::sin; using std::atan2; using std::log; using std::abs;
using std::max; using std::clamp;
struct float3 { float x,y,z; float3(float a,float b,float c):x(a),y(b),z(c){} };
struct float4 { float x,y,z,w; };
float4 _CesiumInstanceGeodetic;
'''
    suffix = '''int main() {
 float e,n,u,s,c,r,e2;
 while(scanf("%f %f %f %f %f %f %f", &e,&n,&u,&s,&c,&r,&e2)==7) {
  _CesiumInstanceGeodetic={s,c,r,e2};
  auto d=CesiumInstanceAngularDelta(float3(e,n,u));
  printf("%.10g %.10g %.10g\\n", d.x,d.y,d.z);
 }
}
'''
    a=6378137.0; b=6356752.3142451793
    rng=np.random.default_rng(71621)
    data=[]; expected=[]
    for flattening in [0.0, 1-b*b/(a*a)]:
        for lat_degrees in [0.0, 51.92, -51.92, 80.0, -80.0]:
            phi=math.radians(lat_degrees); s=math.sin(phi); c=math.cos(phi)
            n0=a/math.sqrt(1-flattening*s*s)
            origin=np.array([n0*c,0.0,n0*(1-flattening)*s])
            basis=np.array([[0,-s,c],[1,0,0],[0,c,s]])
            # Targets within 2 km of the chart origin, at heights 0..500m.
            for _ in range(1000):
                dl=rng.uniform(-2000,2000)/(a*c); dp=rng.uniform(-2000,2000)/a
                p=phi+dp; height=rng.uniform(0,500); n=a/math.sqrt(1-flattening*math.sin(p)**2)
                xyz=np.array([(n+height)*math.cos(p)*math.cos(dl), (n+height)*math.cos(p)*math.sin(dl), (n*(1-flattening)+height)*math.sin(p)])
                enu=basis.T@(xyz-origin)
                data.append([*enu,s,c,n0,flattening])
                expected.append([dl,dp,math.asinh(math.tan(p))-math.asinh(math.tan(phi))])
    text='\n'.join(' '.join(f'{v:.17g}' for v in row) for row in data)+'\n'
    with tempfile.TemporaryDirectory() as directory:
        source=Path(directory)/'projection.cpp'; exe=Path(directory)/'projection'
        source.write_text(prefix+body+suffix)
        subprocess.run([os.environ.get('CXX','c++'),'-std=c++17','-O2',str(source),'-o',str(exe)], check=True)
        result=subprocess.run([str(exe)],input=text,text=True,capture_output=True,check=True)
    actual=np.array([[float(v) for v in line.split()] for line in result.stdout.splitlines()])
    errors=np.abs(actual-np.array(expected))*a
    report={'samples':len(data), 'maximum_projected_error_metres':errors.max(axis=0).tolist(),
            'columns':['longitude','latitude','mercator'], 'tested_horizontal_axis_offsets_metres':2000,
            'unity_shader_compilation':False}
    print(json.dumps(report,indent=2))
    assert np.all(np.isfinite(actual))
    assert errors[:,0].max()<0.01
    assert errors[:,1].max()<0.01
    assert errors[:,2].max()<0.05
    return report

if __name__=='__main__': run()
