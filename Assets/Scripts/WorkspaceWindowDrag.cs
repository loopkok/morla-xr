using UnityEngine;
using UnityEngine.EventSystems;

namespace MORLA.XR
{
    public sealed class WorkspaceWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public Transform target;
        private Vector3 _start, _point;
        private Plane _plane;
        public void OnBeginDrag(PointerEventData data)
        {
            if (target == null || data.pressEventCamera == null) return;
            AppManager.Instance.Session.EnterUiInteraction(); _start = target.position;
            _plane = new Plane(target.forward, _start);
            var ray = data.pressEventCamera.ScreenPointToRay(data.position);
            if (_plane.Raycast(ray, out float distance)) _point = ray.GetPoint(distance);
        }
        public void OnDrag(PointerEventData data)
        {
            if (target == null || data.pressEventCamera == null) return;
            var ray = data.pressEventCamera.ScreenPointToRay(data.position);
            if (_plane.Raycast(ray, out float distance))
                target.position = _start + Vector3.ClampMagnitude(ray.GetPoint(distance) - _point, 0.7f);
        }
    }
}
