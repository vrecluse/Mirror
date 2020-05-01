using UnityEngine;

namespace Mirror.Experimental
{
    [RequireComponent(typeof(Rigidbody))]
    public class NetworkRigidBody : NetworkBehaviour
    {
        [SyncVar(hook = nameof(onIsKinematicChanged))]
        private bool _isKinematic;
        [SyncVar(hook = nameof(onVelocityChanged))]
        private Vector3 _velocity;

        [SerializeField] private bool _ignoreAngularVelocity = true;
        public Rigidbody Body { get; private set; }
        public void Awake()
        {
            Body = GetComponent<Rigidbody>();
        }

        private void Update()
        {
            if (hasAuthority)
            {
                if (isServer)
                {
                    _isKinematic = Body.isKinematic;
                    _velocity = Body.velocity;
                }
                else
                {
                    Debug.LogWarning("NetworkRigidBody only works on server objects");
                }
            }
            else if (_ignoreAngularVelocity)
            {
                Body.angularVelocity = Vector3.zero;
            }
        }

        private void onIsKinematicChanged(bool oldValue, bool newValue)
        {
            Body.isKinematic = newValue;
        }
        private void onVelocityChanged(Vector3 oldValue, Vector3 newValue)
        {
            Body.velocity = newValue;
        }
    }
}
