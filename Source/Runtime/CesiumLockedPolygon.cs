using System.Collections.Generic;
using UnityEngine;

#if SUPPORTS_SPLINES
using Unity.Mathematics;
using UnityEngine.Splines;
#endif

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CesiumForUnity
{
    /// <summary>
    /// Mirrors an external spline into a local <see cref="CesiumCartographicPolygon"/>
    /// so the cutout can stay driven by a spline that lives on a different object.
    /// </summary>
    [ExecuteInEditMode]
#if SUPPORTS_SPLINES
    [RequireComponent(typeof(CesiumCartographicPolygon))]
    [RequireComponent(typeof(SplineContainer))]
    [AddComponentMenu("Cesium/Cesium Locked Polygon")]
#else
    [AddComponentMenu("")]
#endif
    [IconAttribute("Packages/com.cesium.unity/Editor/Resources/Cesium-24x24.png")]
    public class CesiumLockedPolygon : MonoBehaviour
    {
#if SUPPORTS_SPLINES
        [SerializeField]
        private SplineContainer _sourceSpline;

        [SerializeField]
        private CesiumGeoreference _targetGeoreference;

        [SerializeField]
        private List<CesiumPolygonRasterOverlay> _dependentOverlays = new List<CesiumPolygonRasterOverlay>();

        [SerializeField]
        private float _overlayRefreshDelay = 0.1f;

        private CesiumCartographicPolygon _polygon;
        private SplineContainer _targetSpline;
        private CesiumGeoreference _georeference;
        private Matrix4x4 _lastSourceMatrix = Matrix4x4.identity;
        private Matrix4x4 _lastGeoreferenceMatrix = Matrix4x4.identity;
        private int _lastSplineHash;
        private bool _overlaysDirty;
        private double _nextOverlayRefreshTime;

        /// <summary>
        /// The source spline to mirror into the local cartographic polygon.
        /// </summary>
        public SplineContainer sourceSpline
        {
            get => this._sourceSpline;
            set
            {
                this._sourceSpline = value;
                this.SyncLockedPolygon();
            }
        }

        /// <summary>
        /// The georeference whose <c>changed</c> event should trigger a polygon sync.
        /// </summary>
        public CesiumGeoreference targetGeoreference
        {
            get => this._targetGeoreference;
            set
            {
                this._targetGeoreference = value;
                this.UpdateGeoreferenceSubscription(value);
                this.SyncLockedPolygon();
            }
        }

        /// <summary>
        /// The overlays to refresh after the mirrored polygon spline is updated.
        /// </summary>
        public List<CesiumPolygonRasterOverlay> dependentOverlays
        {
            get => this._dependentOverlays;
            set => this._dependentOverlays = value ?? new List<CesiumPolygonRasterOverlay>();
        }

        private void OnEnable()
        {
            this._polygon = this.GetComponent<CesiumCartographicPolygon>();
            this._targetSpline = this.GetComponent<SplineContainer>();
            this.UpdateGeoreferenceSubscription(this._targetGeoreference);
#if UNITY_EDITOR
            EditorApplication.update -= this.HandleEditorUpdate;
            EditorApplication.update += this.HandleEditorUpdate;
            Undo.undoRedoPerformed -= this.HandleUndoRedoPerformed;
            Undo.undoRedoPerformed += this.HandleUndoRedoPerformed;
#endif
            this.SyncLockedPolygon();
        }

        private void OnDisable()
        {
            this.UpdateGeoreferenceSubscription(null);
#if UNITY_EDITOR
            EditorApplication.update -= this.HandleEditorUpdate;
            Undo.undoRedoPerformed -= this.HandleUndoRedoPerformed;
#endif
        }

        private void OnTransformParentChanged()
        {
            if (this._targetGeoreference == null)
            {
                this.UpdateGeoreferenceSubscription();
                this.SyncLockedPolygon();
            }
        }

        private void LateUpdate()
        {
            if (this.HasSourceChanged())
            {
                this.SyncLockedPolygon();
            }

            this.TryRefreshDirtyOverlays();
        }

        private void OnValidate()
        {
            this.UpdateGeoreferenceSubscription(this._targetGeoreference);
            this.SyncLockedPolygon();
        }

        /// <summary>
        /// Synchronizes the local polygon spline from the external source spline
        /// and refreshes any clipping overlays that reference it.
        /// </summary>
        public void SyncLockedPolygon()
        {
            if (this._sourceSpline == null)
            {
                return;
            }

            this.UpdateGeoreferenceSubscription(this._targetGeoreference);

            if (this._polygon == null)
            {
                this._polygon = this.GetComponent<CesiumCartographicPolygon>();
            }

            if (this._targetSpline == null)
            {
                this._targetSpline = this.GetComponent<SplineContainer>();
            }

            IReadOnlyList<Spline> sourceSplines = this._sourceSpline.Splines;
            if (sourceSplines.Count == 0)
            {
                return;
            }

            Spline source = sourceSplines[0];
            Spline mirrored = new Spline();
            mirrored.Closed = source.Closed;

            BezierKnot[] sourceKnots = source.ToArray();
            BezierKnot[] mirroredKnots = new BezierKnot[sourceKnots.Length];
            float4x4 sourceLocalToWorld = this._sourceSpline.transform.localToWorldMatrix;
            Matrix4x4 targetWorldToLocal = this.transform.worldToLocalMatrix;

            for (int i = 0; i < sourceKnots.Length; ++i)
            {
                float3 worldPosition = sourceKnots[i].Transform(sourceLocalToWorld).Position;
                Vector3 localPosition = targetWorldToLocal.MultiplyPoint3x4(worldPosition);
                mirroredKnots[i] = new BezierKnot((float3)localPosition);
            }

            mirrored.Knots = mirroredKnots;
            mirrored.SetTangentMode(TangentMode.Linear);

            IReadOnlyList<Spline> existingSplines = this._targetSpline.Splines;
            if (existingSplines.Count > 0)
            {
                Spline target = existingSplines[0];
                target.Closed = mirrored.Closed;
                target.Knots = mirroredKnots;
                target.SetTangentMode(TangentMode.Linear);

                for (int i = existingSplines.Count - 1; i >= 1; --i)
                {
                    this._targetSpline.RemoveSpline(existingSplines[i]);
                }
            }
            else
            {
                this._targetSpline.AddSpline(mirrored);
            }

            this._lastSourceMatrix = this._sourceSpline.transform.localToWorldMatrix;
            this._lastGeoreferenceMatrix = this.GetGeoreferenceMatrix();
            this._lastSplineHash = this.ComputeSplineHash(source);

            this.MarkOverlaysDirty();
        }

        private void HandleGeoreferenceChanged()
        {
            this.SyncLockedPolygon();
        }

        private void UpdateGeoreferenceSubscription(CesiumGeoreference georeference = null)
        {
            if (this._georeference != null)
            {
                this._georeference.changed -= this.HandleGeoreferenceChanged;
            }

            this._georeference = georeference ?? this.GetComponentInParent<CesiumGeoreference>();

            if (this._georeference != null)
            {
                this._georeference.changed += this.HandleGeoreferenceChanged;
            }
        }

        private bool HasSourceChanged()
        {
            if (this._sourceSpline == null)
            {
                return false;
            }

            IReadOnlyList<Spline> sourceSplines = this._sourceSpline.Splines;
            if (sourceSplines.Count == 0)
            {
                return false;
            }

            return this._lastSourceMatrix != this._sourceSpline.transform.localToWorldMatrix ||
                   this._lastGeoreferenceMatrix != this.GetGeoreferenceMatrix() ||
                   this._lastSplineHash != this.ComputeSplineHash(sourceSplines[0]);
        }

        private Matrix4x4 GetGeoreferenceMatrix()
        {
            return this._georeference != null
                ? this._georeference.transform.localToWorldMatrix
                : Matrix4x4.identity;
        }

        private int ComputeSplineHash(Spline spline)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + spline.Closed.GetHashCode();

                BezierKnot[] knots = spline.ToArray();
                hash = hash * 31 + knots.Length;
                for (int i = 0; i < knots.Length; ++i)
                {
                    hash = hash * 31 + knots[i].Position.GetHashCode();
                    hash = hash * 31 + spline.GetTangentMode(i).GetHashCode();
                }

                return hash;
            }
        }

        private void RefreshDependentOverlays()
        {
            for (int i = 0; i < this._dependentOverlays.Count; ++i)
            {
                CesiumPolygonRasterOverlay overlay = this._dependentOverlays[i];
                if (overlay == null)
                {
                    continue;
                }

                List<CesiumCartographicPolygon> polygons = overlay.polygons;
                if (polygons != null && polygons.Contains(this._polygon))
                {
                    overlay.Refresh();
                }
            }
        }

        private void MarkOverlaysDirty()
        {
            this._overlaysDirty = true;
            this._nextOverlayRefreshTime =
                this.GetCurrentTimeSeconds() + System.Math.Max(0.0f, this._overlayRefreshDelay);
        }

        private void TryRefreshDirtyOverlays()
        {
            if (!this._overlaysDirty || this.GetCurrentTimeSeconds() < this._nextOverlayRefreshTime)
            {
                return;
            }

            this._overlaysDirty = false;
            this.RefreshDependentOverlays();
        }

        private double GetCurrentTimeSeconds()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                return EditorApplication.timeSinceStartup;
            }
#endif
            return Time.realtimeSinceStartupAsDouble;
        }

#if UNITY_EDITOR
        private void HandleEditorUpdate()
        {
            if (!Application.isPlaying && this.HasSourceChanged())
            {
                this.SyncLockedPolygon();
            }

            if (!Application.isPlaying)
            {
                this.TryRefreshDirtyOverlays();
            }
        }

        private void HandleUndoRedoPerformed()
        {
            this.SyncLockedPolygon();
        }
#endif
#endif
    }
}
