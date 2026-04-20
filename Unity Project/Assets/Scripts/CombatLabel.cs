using UnityEngine;
using TMPro;

public class CombatLabel : MonoBehaviour
{
    [Header("Label")]
    public string labelText = "ENTITY";
    public Color labelColor = Color.white;
    public float heightOffset = 1.8f;

    private GameObject labelObj;
    private TextMeshPro tmp;
    private Camera cam;

    void Start()
    {
        cam = Camera.main;
        labelObj = new GameObject("Label_" + labelText);
        labelObj.transform.SetParent(transform);
        labelObj.transform.localPosition = new Vector3(0f, heightOffset, 0f);

        tmp = labelObj.AddComponent<TextMeshPro>();
        tmp.text = labelText;
        tmp.fontSize = 3f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = labelColor;
        tmp.fontStyle = FontStyles.Bold;
    }

    void LateUpdate()
    {
        // Always face camera
        if (cam != null)
            labelObj.transform.rotation =
                Quaternion.LookRotation(labelObj.transform.position - cam.transform.position);
    }
}