using SEEDEditor.Scripting;

public class Temp{
    [SerializeField]    
    float myFloat = 0.0f;

    public void Test()
    {
        myFloat = 1.0f;
        myFloat += 0.5f;
    }
}

public class B{
    [SerializeField]
    Temp t;
    void ABC(){
        t.Test();
    }
    
}