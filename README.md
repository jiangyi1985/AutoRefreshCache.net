# AutoRefreshCache.net

Sample:
``` c#
return AutoRefreshCache.Instance.GetOrRegisterNew<string>("testkey", key =>
             {
                 Thread.Sleep(5000);//some heavy work
                 return "test value created at " + DateTime.UtcNow + " for key: " + key;
             }, TimeSpan.FromSeconds(10));
```
