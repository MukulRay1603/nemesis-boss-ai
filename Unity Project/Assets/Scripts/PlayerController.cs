using UnityEngine;

public class PlayerController : MonoBehaviour
{
    private Rigidbody rb;

    [Header("Movement")]
    public float moveSpeed = 2f;
    public float arenaSize = 9f;

    private float directionTimer = 0f;
    private float directionInterval = 2f;
    private Vector3 moveDirection;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>();

        rb.freezeRotation = true;
        PickNewDirection();
    }

    void FixedUpdate()
    {
        directionTimer += Time.fixedDeltaTime;
        if (directionTimer >= directionInterval)
        {
            PickNewDirection();
            directionTimer = 0f;
        }

        // Move in current direction
        Vector3 newPos = transform.position + moveDirection * moveSpeed * Time.fixedDeltaTime;

        // Clamp to arena
        newPos.x = Mathf.Clamp(newPos.x, -arenaSize, arenaSize);
        newPos.z = Mathf.Clamp(newPos.z, -arenaSize, arenaSize);
        newPos.y = 1f;

        rb.MovePosition(newPos);
    }

    void PickNewDirection()
    {
        float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
        moveDirection = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
    }

    // Called by BossAgent to reset position each episode
    public void ResetPlayer()
    {
        transform.position = new Vector3(0f, 1f, -6f);
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        PickNewDirection();
    }
}