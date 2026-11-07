using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Profiling;

namespace WaterSplash
{
    // Explicit launch extras enable repeatable phone measurements without exposing
    // benchmark controls in the presentation or depending on touch injection.
    public sealed class BottleDeviceAudit : MonoBehaviour
    {
        bool runBenchmark=true;
        bool testSlider;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Attach()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Debug.isDebugBuild) return;
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
            {
                int mode=intent.Call<int>("getIntExtra", "voyageAudit", 0);
                if (mode == 0) return;
                var voyage = FindFirstObjectByType<BottleVoyage>();
                if (voyage != null)
                {
                    var audit=voyage.gameObject.AddComponent<BottleDeviceAudit>();
                    audit.runBenchmark=mode==1; audit.testSlider=mode==3;
                }
            }
#endif
        }

        IEnumerator Start()
        {
            var voyage = GetComponent<BottleVoyage>();
            yield return new WaitForSecondsRealtime(2);
            if (voyage == null || voyage.Error != null) yield break;
            if(testSlider)
            {
                yield return VerifySlider(voyage);
                yield break;
            }
            if(runBenchmark)
            {
                if (!voyage.fluid.StartTransferBenchmark(6)) yield break;
                while (voyage.fluid.IsCalibrating) yield return null;
            }
            var intervals = new List<double>();
            var sampler = Recorder.Get("MLS-MPM GPU/Mass + momentum");
            sampler.enabled = true;
            long gpuNs = 0; int gpuBlocks = 0;
            double start = Time.realtimeSinceStartupAsDouble;
            double simulated = voyage.fluid.SimulatedSeconds;
            for (int i = 0; i < 1200; i++)
            {
                yield return null;
                intervals.Add(Time.unscaledDeltaTime * 1000.0);
                gpuNs += sampler.gpuElapsedNanoseconds;
                gpuBlocks += sampler.gpuSampleBlockCount;
            }
            intervals.Sort();
            double elapsed = Time.realtimeSinceStartupAsDouble - start;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "BOTTLE DEVICE AUDIT: particles={0} optimized={1} fps={2:F2} p95CpuInterval={3:F3}ms p99CpuInterval={4:F3}ms simulationRatio={5:F4} backlog={6:F4}s p2gGpuPerSample={7:F4}ms gpuSamples={8} error='{9}'",
                voyage.fluid.particleCount, voyage.fluid.optimizeTransfers, intervals.Count / elapsed,
                intervals[(int)(intervals.Count * .95)], intervals[(int)(intervals.Count * .99)],
                (voyage.fluid.SimulatedSeconds - simulated) / elapsed, voyage.BacklogSeconds,
                gpuBlocks > 0 ? gpuNs / 1e6 / gpuBlocks : -1, gpuBlocks, voyage.Error));
            sampler.enabled = false;
        }

        IEnumerator VerifySlider(BottleVoyage voyage)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            float scale=Mathf.Max(.5f,Mathf.Min(Screen.width/1280f,Screen.height/800f));
            float x=Screen.safeArea.xMax/scale-610+6+7;
            float y=(Screen.height-Screen.safeArea.yMin)/scale-88+9+7;
            Debug.Log($"BOTTLE SLIDER AUDIT: screen={Screen.width}x{Screen.height} safe={Screen.safeArea} point={new Vector2(x,y)*scale}");
            foreach(int tier in new[]{0,3,1,2})
            {
                float start=x+264*BottleVoyage.SliderForParticleCount(voyage.RequestedParticleCount)/3;
                float end=x+264*tier/3;
                SendTouch(0,new Vector2(start,y)*scale);
                yield return new WaitForSecondsRealtime(.1f);
                Debug.Log($"BOTTLE SLIDER AUDIT: touches={Input.touchCount} mouse={Input.mousePosition}");
                for(int step=1;step<=8;step++)
                {
                    SendTouch(2,new Vector2(Mathf.Lerp(start,end,step/8f),y)*scale);
                    yield return null;
                }
                SendTouch(1,new Vector2(end,y)*scale);
                yield return new WaitForSecondsRealtime(1);
                int expected=BottleVoyage.ParticleCountForSlider(tier);
                if(voyage.RequestedParticleCount!=expected||voyage.fluid.particleCount!=expected||voyage.Error!=null)
                {
                    Debug.LogError($"BOTTLE SLIDER AUDIT: failed expected={expected} requested={voyage.RequestedParticleCount} active={voyage.fluid.particleCount} error='{voyage.Error}'");
                    yield break;
                }
                Debug.Log($"BOTTLE SLIDER AUDIT: passed particles={expected}");
            }
#else
            yield break;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static void SendTouch(int action,Vector2 point)
        {
            using(var player=new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using(var activity=player.GetStatic<AndroidJavaObject>("currentActivity"))
                activity.Call("runOnUiThread",new TouchEvent(action,point));
        }

        // Dispatch within this app's window, using Android's normal input path.
        sealed class TouchEvent:AndroidJavaProxy
        {
            readonly int action; readonly Vector2 point;
            static long downTime;
            public TouchEvent(int action,Vector2 point):base("java.lang.Runnable")
            {
                this.action=action;this.point=point;
            }
            public void run()
            {
                using(var player=new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using(var activity=player.GetStatic<AndroidJavaObject>("currentActivity"))
                using(var clock=new AndroidJavaClass("android.os.SystemClock"))
                using(var events=new AndroidJavaClass("android.view.MotionEvent"))
                {
                    long now=clock.CallStatic<long>("uptimeMillis");if(action==0)downTime=now;
                    using(var motion=events.CallStatic<AndroidJavaObject>("obtain",downTime,now,action,point.x,point.y,0))
                    {
                        motion.Call("setSource",0x1002);
                        activity.Call<bool>("dispatchTouchEvent",motion);motion.Call("recycle");
                    }
                }
            }
        }
#endif
    }
}
