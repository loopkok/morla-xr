using TMPro;
using UnityEngine;

namespace MORLA.XR
{
    public sealed class MorlaTextStyle : MonoBehaviour
    {
        public float baseSize = 27;
        public bool secondary;
        public void Apply(float scale) { var text = GetComponent<TMP_Text>(); if (text != null) text.fontSize = baseSize * scale; }
    }
}
