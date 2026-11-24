using UnityEngine;

namespace Chimera.Core
{
    /// Seguimiento suave de cámara táctica con desplazamiento acotado
    /// para mantener toda la arena octogonal legible en pantalla.
    public class ArenaCameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 baseOffset = new Vector3(0f, 16f, -16.5f);
        [SerializeField] private float followWeight = 0.25f;
        [SerializeField] private float smoothSpeed = 6f;

        private void LateUpdate()
        {
            if (target == null) return;

            // Desplazamiento sutil (25% de la posición del jugador) para mantener el centro táctico
            Vector3 desiredPos = baseOffset + new Vector3(target.position.x * followWeight, 0f, target.position.z * followWeight);
            transform.position = Vector3.Lerp(transform.position, desiredPos, smoothSpeed * Time.deltaTime);
        }
    }
}