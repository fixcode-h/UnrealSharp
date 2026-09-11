using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnrealSharp.Core.Attributes;
using UnrealSharp.Core.Marshallers;

namespace UnrealSharp.Core;

public static class UnmanagedCallbacks
{
    [UnmanagedCallersOnly]
    public static unsafe IntPtr CreateNewManagedObject(IntPtr nativeObject, IntPtr typeHandlePtr, char** error)
    {
        try
        {
            if (nativeObject == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(nativeObject));
            }
            
            Type? type = GCHandleUtilities.GetObjectFromHandlePtr<Type>(typeHandlePtr);
            
            if (type == null)
            {
                throw new InvalidOperationException("The provided type handle does not point to a valid type.");
            }

            return UnrealSharpObject.Create(type, nativeObject);
        }
        catch (Exception ex)
        {
            LogUnrealSharpCore.LogError($"Failed to create new managed object: {ex.Message}");
            *error = (char*)Marshal.StringToHGlobalUni(ex.ToString());
        }

        return IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    public static IntPtr CreateNewManagedObjectWrapper(IntPtr managedObjectHandle, IntPtr typeHandlePtr)
    {
        try
        {
            if (managedObjectHandle == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(managedObjectHandle));
            }
            
            Type? type = GCHandleUtilities.GetObjectFromHandlePtr<Type>(typeHandlePtr);
            
            if (type is null)
            {
                throw new InvalidOperationException("The provided type handle does not point to a valid type.");
            }
            
            object? managedObject = GCHandleUtilities.GetObjectFromHandlePtr<object>(managedObjectHandle);
            if (managedObject is null)
            {
                throw new InvalidOperationException("The provided managed object handle does not point to a valid object.");
            }

            MethodInfo? wrapMethod = type.GetMethod("Wrap", BindingFlags.Public | BindingFlags.Static);
            if (wrapMethod is null)
            {
                throw new InvalidOperationException("The provided type does not have a static Wrap method.");
            }
            
            object? createdObject = wrapMethod.Invoke(null, [managedObject]);
            if (createdObject is null)
            {
                throw new InvalidOperationException("The Wrap method did not return a valid object.");
            }

            return GCHandle.ToIntPtr(GCHandleUtilities.AllocateStrongPointer(createdObject, createdObject.GetType().Assembly));
        }
        catch (Exception ex)
        {
            LogUnrealSharpCore.LogError($"Failed to create new managed object: {ex.Message}");
        }

        return IntPtr.Zero;
    }
    
    [UnmanagedCallersOnly]
    public static unsafe IntPtr GetManagedMethod(IntPtr typeHandlePtr, char* methodName)
    {
        try
        {
            Type? type = GCHandleUtilities.GetObjectFromHandlePtr<Type>(typeHandlePtr);
            
            if (type == null)
            {
                throw new Exception("Invalid type handle");
            }
            
            string methodNameString = new string(methodName);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            Type? currentType = type;
            
            while (currentType != null)
            {
                MethodInfo? method = currentType.GetMethod(methodNameString, flags);

                if (method != null)
                {
                    IntPtr functionPtr = method.MethodHandle.GetFunctionPointer();
                    GCHandle methodHandle = GCHandleUtilities.AllocateStrongPointer(functionPtr, type.Assembly);
                    return GCHandle.ToIntPtr(methodHandle);
                }
                
                currentType = currentType.BaseType;
            }

            return IntPtr.Zero;
        }
        catch (Exception e)
        {
            LogUnrealSharpCore.LogError($"Exception while trying to look up managed method: {e.Message}");
        }

        return IntPtr.Zero;
    }
    
    [UnmanagedCallersOnly]
    public static void InitializeStruct(IntPtr structHandle, IntPtr buffer)
    {
        try
        {
            Type? structType = GCHandleUtilities.GetObjectFromHandlePtr<Type>(structHandle);
            
            if (structType == null)
            {
                throw new Exception("Invalid struct type handle");
            }
            
            object? structInstance = Activator.CreateInstance(structType);
            
            if (structInstance == null)
            {
                throw new Exception("Failed to create struct instance");
            }

            MethodInfo? methodInfo = structType.GetMethod("ToNative");
            
            if (methodInfo == null)
            {
                throw new Exception("The struct type does not have a ToNative method");
            }
            
            methodInfo.Invoke(structInstance, [buffer]);
        }
        catch (Exception e)
        {
            LogUnrealSharpCore.LogError($"Exception while trying to initialize struct: {e.Message}");
        }
    }
    
    // 每个程序集只扫描一次，之后按原生类型名 O(1) 查表。
    // 用 ConditionalWeakTable：键(Assembly)被回收时索引自动消失，不会把可回收 ALC 钉在内存里。
    // （Assembly 之间按引用相等，热重载后是新实例，自然重建索引。）
    private static readonly ConditionalWeakTable<Assembly, GeneratedTypeIndex> s_generatedTypeIndexes = new();

    [UnmanagedCallersOnly]
    public static unsafe IntPtr GetManagedTypeHandle(IntPtr assemblyHandle, char* fullTypeName)
    {
        // 注意：本方法是 [UnmanagedCallersOnly]，异常一旦穿过原生边界，CLR 会直接终止进程
        // （表现为 "Fatal error. Internal CLR error. (0x80131506)"）。所以这里必须兜住所有异常，
        // 不能只 catch TypeLoadException。
        IntPtr cachedHandle = IntPtr.Zero;

        try
        {
            string fullTypeNameString = new string(fullTypeName);
            Assembly? loadedAssembly = GCHandleUtilities.GetObjectFromHandlePtr<Assembly>(assemblyHandle);

            if (loadedAssembly == null)
            {
                throw new InvalidOperationException("The provided assembly handle does not point to a valid assembly.");
            }

            GeneratedTypeIndex index = s_generatedTypeIndexes.GetValue(loadedAssembly, static assembly => GeneratedTypeIndex.Build(assembly));

            if (index.TryGetTypeHandle(fullTypeNameString, out cachedHandle))
            {
                return cachedHandle;
            }
        }
        catch (Exception ex)
        {
            LogUnrealSharpCore.LogError($"Failed to resolve managed type handle for '{new string(fullTypeName)}': {ex}");
            return IntPtr.Zero;
        }

        // 未找到时不缓存失败结果：否则热重载后可能一直命中失败，而每次调用又要重建一次索引。
        return cachedHandle;
    }

    /// <summary>
    /// 单个程序集内 "GeneratedType 的 FullName -> 托管类型句柄" 的一次性索引。
    /// </summary>
    private sealed class GeneratedTypeIndex
    {
        private readonly ConcurrentDictionary<string, IntPtr> _handlesByFullName = new(StringComparer.Ordinal);

        public static GeneratedTypeIndex Build(Assembly assembly)
        {
            GeneratedTypeIndex index = new GeneratedTypeIndex();

            // GetTypes() 在部分类型加载失败时会抛 ReflectionTypeLoadException；由调用方统一兜住。
            foreach (Type type in assembly.GetTypes())
            {
                // 用 CustomAttributeData.GetCustomAttributes(type) 而不是 type.CustomAttributes：
                // 前者不会实例化特性对象，开销显著更低（这里要对数万个类型各做一次）。
                IList<CustomAttributeData> attributes = CustomAttributeData.GetCustomAttributes(type);

                for (int i = 0; i < attributes.Count; i++)
                {
                    CustomAttributeData attributeData = attributes[i];

                    // 与上游一致地按 FullName 比较，避免跨程序集上下文时 Type 身份不等价。
                    if (attributeData.AttributeType.FullName != typeof(GeneratedTypeAttribute).FullName)
                    {
                        continue;
                    }

                    if (attributeData.ConstructorArguments.Count != 2)
                    {
                        continue;
                    }

                    if (attributeData.ConstructorArguments[1].Value is not string fullName)
                    {
                        continue;
                    }

                    // 原生侧 FCSFieldName::GetFullName() 查的就是 FullName（"{Namespace}.{EngineName}"，
                    // 与 TypeDeclarationBuilder 生成 [GeneratedType(engineName, namespace.engineName)] 的第二参一致）。
                    // 引擎名另外登记一份作为备用键，避免将来调用方改用 EngineName 时查不到。
                    index._handlesByFullName.TryAdd(fullName, GCHandle.ToIntPtr(GCHandleUtilities.AllocateStrongPointer(type, assembly)));

                    if (attributeData.ConstructorArguments[0].Value is string engineName && !string.IsNullOrEmpty(engineName))
                    {
                        index._handlesByFullName.TryAdd(engineName, GCHandle.ToIntPtr(GCHandleUtilities.AllocateStrongPointer(type, assembly)));
                    }
                }
            }

            return index;
        }

        public bool TryGetTypeHandle(string fullTypeName, out IntPtr handle)
        {
            return _handlesByFullName.TryGetValue(fullTypeName, out handle);
        }
    }
    
    [UnmanagedCallersOnly]
    public static unsafe int InvokeManagedMethod(IntPtr managedObjectHandle,
        IntPtr methodHandlePtr, 
        IntPtr argumentsBuffer, 
        IntPtr returnValueBuffer, 
        IntPtr exceptionTextBuffer)
    {
        try
        {
            IntPtr methodHandle = GCHandleUtilities.GetObjectFromHandlePtrFast<IntPtr>(methodHandlePtr)!;
            object managedObject = GCHandleUtilities.GetObjectFromHandlePtrFast<object>(managedObjectHandle)!;
            delegate*<object, IntPtr, IntPtr, void> methodPtr = (delegate*<object, IntPtr, IntPtr, void>) methodHandle;
            methodPtr(managedObject, argumentsBuffer, returnValueBuffer);
            return 0;
        }
        catch (Exception ex)
        {
            StringMarshaller.ToNative(exceptionTextBuffer, 0, ex.ToString());
            LogUnrealSharpCore.LogError($"Exception during InvokeManagedMethod: {ex.Message}");
            return 1;
        }
    }

    [UnmanagedCallersOnly]
    public static void InvokeDelegate(IntPtr delegatePtr)
    {
        try
        {
            Delegate? foundDelegate = GCHandleUtilities.GetObjectFromHandlePtr<Delegate>(delegatePtr);
            
            if (foundDelegate == null)
            {
                throw new Exception("Invalid delegate handle");
            }

            foundDelegate.DynamicInvoke();
        }
        catch (Exception ex)
        {
            LogUnrealSharpCore.LogError($"Exception during InvokeDelegate: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly]
    public static void Dispose(IntPtr handle, IntPtr assemblyHandle)
    {
        GCHandle foundHandle = GCHandle.FromIntPtr(handle);
        
        if (!foundHandle.IsAllocated)
        {
            return;
        }
        
        if (foundHandle.Target is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Assembly? foundAssembly = GCHandleUtilities.GetObjectFromHandlePtr<Assembly>(assemblyHandle);
        GCHandleUtilities.Free(foundHandle, foundAssembly);
    }

    [UnmanagedCallersOnly]
    public static void FreeHandle(IntPtr handle)
    {
        GCHandle foundHandle = GCHandle.FromIntPtr(handle);
        if (!foundHandle.IsAllocated) return;
        
        if (foundHandle.Target is IDisposable disposable)
        {
            disposable.Dispose();
        }
            
        foundHandle.Free();
    }
}