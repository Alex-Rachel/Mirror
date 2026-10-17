using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mirror.Examples.RelayLobby
{
    [RequireComponent(typeof(NetworkTransformReliable))]
    public sealed class RelayLobbyPlayer : NetworkBehaviour
    {
        [SyncVar] public string displayName = "Player";
        [SyncVar(hook = nameof(OnColorChanged))] public Color32 playerColor = Color.white;
        [SyncVar] public int score;
        [SyncVar] public bool roomStarted;
        public Renderer visual;
        public TextMesh label;
        [SerializeField] float moveSpeed = 4f;
        [SerializeField] float worldLimit = 10f;
        [SerializeField] float inputTimeout = 0.35f;
        Vector2 serverInput;
        double inputTime;
        float nextInput;
        MaterialPropertyBlock colorBlock;

        // Explicitly enabled by the process-test bootstrap only.
        public Vector2 TestInput { get; set; }

        public override void OnStartServer()
        {
            playerColor = Color.HSVToRGB((netId * 0.271f) % 1f, 0.65f, 1f);
        }
        public override void OnStartClient() => OnColorChanged(default, playerColor);

        void Update()
        {
            if (label != null)
            {
                label.text = $"{displayName}\n{score} pts  |  {(roomStarted ? "PLAY" : "WAIT")}";
                if (Camera.main != null) label.transform.rotation = Camera.main.transform.rotation;
            }
            if (!isLocalPlayer || !NetworkClient.ready || Time.unscaledTime < nextInput) return;
            nextInput = Time.unscaledTime + 0.05f;
            bool typing = EventSystem.current != null &&
                EventSystem.current.currentSelectedGameObject != null &&
                EventSystem.current.currentSelectedGameObject.GetComponent<InputField>() != null;
            Vector2 input = typing || !roomStarted ? Vector2.zero :
                TestInput != Vector2.zero ? TestInput :
                new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            CmdMove(Vector2.ClampMagnitude(input, 1f));
        }

        [Command(channel = Channels.Unreliable)]
        void CmdMove(Vector2 input)
        {
            if (float.IsNaN(input.x) || float.IsInfinity(input.x) ||
                float.IsNaN(input.y) || float.IsInfinity(input.y)) return;
            serverInput = roomStarted ? Vector2.ClampMagnitude(input, 1f) : Vector2.zero;
            inputTime = NetworkTime.localTime;
        }

        [ServerCallback]
        void FixedUpdate()
        {
            if (!roomStarted || NetworkTime.localTime - inputTime > inputTimeout ||
                serverInput.sqrMagnitude < 0.001f) return;
            Vector3 position = transform.position +
                new Vector3(serverInput.x, 0, serverInput.y) * (moveSpeed * Time.fixedDeltaTime);
            position.x = Mathf.Clamp(position.x, -worldLimit, worldLimit);
            position.z = Mathf.Clamp(position.z, -worldLimit, worldLimit);
            transform.position = position;
            score = Mathf.Min(999999, score + 1);
        }

        [Server]
        public void SetServerDisplayName(string value) =>
            displayName = string.IsNullOrWhiteSpace(value) ? "Player" : value.Trim();

        void OnColorChanged(Color32 _, Color32 value)
        {
            if (visual == null) return;
            colorBlock = colorBlock ?? new MaterialPropertyBlock();
            colorBlock.SetColor("_Color", value);
            colorBlock.SetColor("_BaseColor", value);
            visual.SetPropertyBlock(colorBlock);
        }
    }
}
