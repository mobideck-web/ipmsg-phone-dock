package app.lantern.lan

import android.Manifest
import android.app.AlertDialog
import android.content.pm.PackageManager
import android.graphics.SurfaceTexture
import android.hardware.camera2.*
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Size
import android.view.Surface
import android.view.TextureView
import android.view.WindowManager
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.lifecycle.Lifecycle
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors

/** Camera and microphone sharing are foreground-only. The owner alone enables sound. */
class LiveCameraActivity : ComponentActivity() {
    private val model by lazy { Model.get(this) }
    private val handler = Handler(Looper.getMainLooper())
    private val cameraThread=android.os.HandlerThread("IPMsg-camera-control").apply{start()}
    private val cameraHandler=Handler(cameraThread.looper)
    private lateinit var previewBox: android.widget.FrameLayout
    private lateinit var preview: TextureView
    private var frameReader: android.media.ImageReader? = null
    @Volatile private var frameRotation=90
    @Volatile private var availableFrames=0L
    @Volatile private var lastPublishedFrameAt=0L
    private var lastFrameAt=0L
    private var frameBuffer:ByteArray?=null
    private val frameWorkspace=CameraFrames.Workspace()
    private val jpegBuffer=ByteArrayOutputStream(64*1024)
    private var uiStatus by mutableStateOf("Preparing camera…")
    private var uiReady by mutableStateOf(false)
    private var uiRecording by mutableStateOf(false)
    private var uiFlashReady by mutableStateOf(false)
    private var uiFlashOn by mutableStateOf(false)
    private var uiMicOn by mutableStateOf(false)
    private var uiSeconds by mutableIntStateOf(600)
    private var uiNotice by mutableStateOf("")
    private var uiQuality by mutableIntStateOf(50)
    private var statusUntil=0L
    private var permissionDialogOpen=false
    private var audio: CameraAudio?=null
    @Volatile private var audioGeneration=0
    @Volatile private var microphoneOn=false
    private var networkCallback: android.net.ConnectivityManager.NetworkCallback? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var surface: Surface? = null
    private var server: LiveCameraServer? = null
    private var cameraIdentity: CameraIdentity? = null
    private var cameraAddress: String? = null
    private var cameraNetworkIdentity: String? = null
    private val deadline = android.os.SystemClock.elapsedRealtime() + 600_000
    @Volatile private var lastViewerAction=""
    @Volatile private var viewerActionAt=0L
    private var size = Size(640, 480)
    private var previewSize = Size(640,480)
    private var orientation = 90
    private var epoch = 0
    @Volatile private var opening = false
    @Volatile private var closing = false
    @Volatile private var recording = false
    @Volatile private var flashlightAvailable = false
    @Volatile private var flashlightOn = false
    @Volatile private var remoteStatus = "Live · microphone off"
    @Volatile private var frameBusy = false
    private val encoder = Executors.newSingleThreadExecutor()
    @Volatile private var quality = CameraQuality.SMOOTH
    @Volatile private var qualityPercent=50
    private val peerIp get() = intent.getStringExtra("peerIp").orEmpty()
    private val peerUser get() = intent.getStringExtra("peerUser").orEmpty()
    private val permission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        permissionDialogOpen=false
        if (granted) startCamera() else { message("Camera permission declined"); finish() }
    }
    private val microphonePermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        permissionDialogOpen=false
        // A permission result is never allowed to revive a background or ended session.
        if (granted && lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) && window.decorView.hasWindowFocus()) startMicrophone()
        else if (!closing) message(if(granted) "Microphone allowed. Tap Mic to share sound." else "Microphone permission declined")
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        qualityPercent=model.preferences.getInt("camera-quality-percent",50).coerceIn(10,100)
        quality=if(qualityPercent<=50)CameraQuality.SMOOTH else CameraQuality.CLEAR
        uiQuality=qualityPercent
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_SECURE)
        WindowCompat.setDecorFitsSystemWindows(window,false)
        window.statusBarColor=android.graphics.Color.rgb(16,25,22)
        window.navigationBarColor=android.graphics.Color.rgb(16,25,22)
        WindowCompat.getInsetsController(window,window.decorView).apply { isAppearanceLightStatusBars=false;isAppearanceLightNavigationBars=false }
        preview = TextureView(this)
        previewBox=android.widget.FrameLayout(this).apply{setBackgroundColor(android.graphics.Color.BLACK);addView(preview,android.widget.FrameLayout.LayoutParams(-1,-1,android.view.Gravity.CENTER))}
        previewBox.addOnLayoutChangeListener{_,_,_,_,_,_,_,_,_->transform()}
        val connectivity=getSystemService(android.net.ConnectivityManager::class.java)
        networkCallback=object:android.net.ConnectivityManager.NetworkCallback(){
            private fun checkCameraAddress() {
                handler.post {
                    if(!closing && cameraNetworkChanged()) {
                        message("Local network changed")
                        shutdown()
                        finish()
                    }
                }
            }
            override fun onAvailable(network:android.net.Network){checkCameraAddress()}
            override fun onLost(network:android.net.Network){checkCameraAddress()}
            override fun onLinkPropertiesChanged(network:android.net.Network,properties:android.net.LinkProperties){checkCameraAddress()}
        }.also{connectivity.registerDefaultNetworkCallback(it)}
        preview.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(texture: SurfaceTexture, width: Int, height: Int) { startCamera() }
            override fun onSurfaceTextureSizeChanged(texture: SurfaceTexture, width: Int, height: Int) { transform() }
            override fun onSurfaceTextureDestroyed(texture: SurfaceTexture): Boolean { shutdown(); return true }
            override fun onSurfaceTextureUpdated(texture: SurfaceTexture) { }
        }
        setContent { IPMsgTheme(dark=true) { CameraScreen() } }
        handler.postDelayed({ if (!closing) { message("Camera session expired"); finish() } }, 600_000)
    }

    @Composable private fun CameraScreen() {
        var chatOpen by remember { mutableStateOf(false) }
        var qualityOpen by remember { mutableStateOf(false) }
        var menuOpen by remember { mutableStateOf(false) }
        val chatState=rememberSaveableStateHolder()
        BackHandler { if(chatOpen) chatOpen=false else finish() }
        Surface(Modifier.fillMaxSize(),color=MaterialTheme.colorScheme.background) {
            BoxWithConstraints(Modifier.fillMaxSize().safeDrawingPadding().imePadding().padding(horizontal=16.dp)) {
                val compactHeight=(maxHeight*0.23f).coerceIn(88.dp,176.dp)
                Column(Modifier.fillMaxSize(),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                    Row(Modifier.fillMaxWidth().heightIn(min=56.dp),verticalAlignment=Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text("Live camera",style=MaterialTheme.typography.titleLarge,fontWeight=FontWeight.SemiBold)
                            Text(model.devices.label(peerIp,peerUser),style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant,maxLines=1,overflow=TextOverflow.Ellipsis)
                        }
                        Box {
                            IconButton(onClick={menuOpen=true},modifier=Modifier.semantics{contentDescription="Camera options"}) { CameraSymbol("more") }
                            DropdownMenu(expanded=menuOpen,onDismissRequest={menuOpen=false}) {
                                DropdownMenuItem(text={Text("Send viewing link")},enabled=uiReady,onClick={menuOpen=false;shareLink()})
                                DropdownMenuItem(text={Text("Verify connection")},enabled=uiReady,onClick={menuOpen=false;showCertificate()})
                            }
                        }
                    }
                    Box((if(chatOpen) Modifier.height(compactHeight) else Modifier.weight(1f)).fillMaxWidth().clip(RoundedCornerShape(24.dp)).background(Color.Black)) {
                        // Keep this AndroidView at one composition location: toggling Chat never recreates its SurfaceTexture.
                        AndroidView(factory={previewBox},modifier=Modifier.fillMaxSize(),update={it.post{transform()}})
                        Row(Modifier.fillMaxWidth().padding(12.dp),horizontalArrangement=Arrangement.SpaceBetween,verticalAlignment=Alignment.CenterVertically) {
                            Surface(color=Color(0xdd101916),shape=RoundedCornerShape(20.dp)) {
                                Text(if(uiReady) "● Live · ${uiSeconds/60}:${(uiSeconds%60).toString().padStart(2,'0')}" else "Connecting…",Modifier.padding(horizontal=12.dp,vertical=8.dp),style=MaterialTheme.typography.labelMedium,color=if(uiReady)MaterialTheme.colorScheme.primary else Color.White)
                            }
                            if(uiRecording) Surface(color=Color(0xdd5c171b),shape=RoundedCornerShape(20.dp)) {
                                Text("● Recording",Modifier.padding(horizontal=12.dp,vertical=8.dp),style=MaterialTheme.typography.labelMedium,color=Color.White)
                            }
                        }
                        if(!chatOpen) Surface(Modifier.align(Alignment.BottomStart).padding(12.dp),color=Color(0xdd101916),shape=RoundedCornerShape(20.dp)) {
                            Text(if(uiMicOn) "Microphone shared" else "Microphone off",Modifier.padding(horizontal=12.dp,vertical=8.dp),style=MaterialTheme.typography.labelMedium,color=if(uiMicOn)MaterialTheme.colorScheme.primary else Color.White)
                        }
                    }
                    if(uiNotice.isNotBlank() || uiStatus.isNotBlank()) Text(uiNotice.ifBlank{uiStatus},Modifier.fillMaxWidth(),style=MaterialTheme.typography.bodySmall,color=if(uiRecording)MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,maxLines=2,overflow=TextOverflow.Ellipsis)
                    Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                        CameraControl("mic",if(uiMicOn)"Mic on" else "Mic off",uiMicOn,uiReady,Modifier.weight(1f)){toggleMicrophone()}
                        CameraControl("flash",if(uiFlashOn)"Light on" else "Light",uiFlashOn,uiFlashReady,Modifier.weight(1f)){toggleFlashlight()}
                        CameraControl("quality","$uiQuality%",qualityOpen,uiReady,Modifier.weight(1f)){qualityOpen=!qualityOpen}
                        CameraControl("chat","Chat",chatOpen,true,Modifier.weight(1f)){chatOpen=!chatOpen;qualityOpen=false}
                    }
                    if(qualityOpen) Surface(shape=RoundedCornerShape(20.dp),color=MaterialTheme.colorScheme.surfaceContainerLow) {
                        Column(Modifier.padding(horizontal=16.dp,vertical=8.dp)) {
                            Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween) { Text("Stream quality",style=MaterialTheme.typography.labelLarge);Text("$uiQuality%",style=MaterialTheme.typography.labelLarge) }
                            Slider(value=uiQuality.toFloat(),onValueChange={value->uiQuality=value.toInt();qualityPercent=uiQuality;quality=if(uiQuality<=50)CameraQuality.SMOOTH else CameraQuality.CLEAR},valueRange=10f..100f,onValueChangeFinished={model.preferences.edit().putInt("camera-quality-percent",uiQuality).apply()},modifier=Modifier.height(40.dp))
                            Text("Smoother at lower quality · updates this link",style=MaterialTheme.typography.labelSmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    if(chatOpen) chatState.SaveableStateProvider("$peerIp\u0000$peerUser") {
                        LiveCameraChat(model,peerIp,peerUser,Modifier.weight(1f).fillMaxWidth())
                    }
                    Row(Modifier.fillMaxWidth().padding(bottom=8.dp),horizontalArrangement=Arrangement.spacedBy(12.dp)) {
                        OutlinedButton(onClick={shareLink()},enabled=uiReady,modifier=Modifier.weight(1f).heightIn(min=48.dp)) { Text("Send link") }
                        Button(onClick={shutdown();finish()},colors=ButtonDefaults.buttonColors(containerColor=MaterialTheme.colorScheme.errorContainer,contentColor=MaterialTheme.colorScheme.onErrorContainer),modifier=Modifier.weight(1f).heightIn(min=48.dp)) { Text("Stop camera") }
                    }
                }
            }
        }
    }

    private fun showCertificate() {
        val identity=cameraIdentity ?: return message("Camera is not ready")
        AlertDialog.Builder(this).setTitle("Verify this camera").setMessage("SHA-256 certificate fingerprint:\n${identity.fingerprint}\n\nIn Edge or Chrome, inspect this site's certificate and compare every group with this phone before allowing a site-specific exception. Do not disable certificate validation globally or install a general CA. Reverify if the IP or certificate changes. Managed browsers may refuse local certificates; use photo/video sharing instead.").setPositiveButton("Close",null).show().findViewById<TextView>(android.R.id.message)?.setTextIsSelectable(true)
    }
    private fun message(value: String) { handler.post { if(!isDestroyed) {uiStatus=value;statusUntil=android.os.SystemClock.elapsedRealtime()+6000} } }
    private fun confirm(text: String, action: () -> Unit) {
        if (closing || isFinishing) return
        AlertDialog.Builder(this).setTitle("Owner permission").setMessage(text)
            .setPositiveButton("Allow") { _, _ -> if (!closing) action() }
            .setNegativeButton("Cancel", null).show()
    }

    @Suppress("MissingPermission", "DEPRECATION")
    private fun startCamera() {
        if(Thread.currentThread()!=cameraThread) {cameraHandler.post{startCamera()};return}
        if (closing || opening || camera != null || !preview.isAvailable) return
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) { handler.post{if(!closing){permissionDialogOpen=true;permission.launch(Manifest.permission.CAMERA)}}; return }
        runCatching {
            require(peerIp.isNotBlank() && model.devices.approved(peerIp,peerUser)) { "Device unavailable" }
            val manager = getSystemService(CameraManager::class.java)
            val id = manager.cameraIdList.firstOrNull { manager.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK } ?: manager.cameraIdList.first()
            val chars = manager.getCameraCharacteristics(id)
            flashlightAvailable=chars.get(CameraCharacteristics.FLASH_INFO_AVAILABLE)==true
            flashlightOn=false
            updateFlashlightControl(false)
            orientation = chars.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90
            val map = requireNotNull(chars.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP))
            val frameSizes=map.getOutputSizes(android.graphics.ImageFormat.YUV_420_888).toSet()
            val sizes = map.getOutputSizes(SurfaceTexture::class.java).filter { it in frameSizes }
            size = sizes.maxByOrNull { it.width * it.height } ?: error("No compatible camera size")
            // Keep the phone preview at PREVIEW size while the YUV stream uses its native output.
            val previewSizes=map.getOutputSizes(SurfaceTexture::class.java).filter{it.width<=1920 && it.height<=1080}
            previewSize=previewSizes.filter{kotlin.math.abs(it.width.toDouble()/it.height-size.width.toDouble()/size.height)<0.03}.maxByOrNull{it.width*it.height}
                ?: previewSizes.maxByOrNull{it.width*it.height} ?: size
            preview.surfaceTexture!!.setDefaultBufferSize(previewSize.width, previewSize.height)
            surface = Surface(preview.surfaceTexture)
            frameReader=android.media.ImageReader.newInstance(size.width,size.height,android.graphics.ImageFormat.YUV_420_888,3).also { reader->
                reader.setOnImageAvailableListener({source->
                    val image=runCatching{source.acquireLatestImage()}.getOrNull() ?: return@setOnImageAvailableListener
                    availableFrames++
                    val now=android.os.SystemClock.elapsedRealtime()
                    if(closing || frameBusy || now-lastFrameAt<quality.intervalMs){image.close();return@setOnImageAvailableListener}
                    frameBusy=true;lastFrameAt=now
                    try{encoder.execute{
                        try{
                            val rotation=frameRotation;val percent=qualityPercent
                            val sw=((image.width*percent/100) and -2).coerceAtLeast(2);val sh=((image.height*percent/100) and -2).coerceAtLeast(2)
                            val count=sw*sh*3/2
                            val output=frameBuffer?.takeIf{it.size==count} ?: ByteArray(count).also{frameBuffer=it}
                            val planes=image.planes.map{CameraFrames.Plane(it.buffer,it.rowStride,it.pixelStride)}
                            val (w,h)=CameraFrames.packScaled(image.width,image.height,planes[0],planes[1],planes[2],rotation,percent,output,frameWorkspace)
                            jpegBuffer.reset()
                            check(android.graphics.YuvImage(output,android.graphics.ImageFormat.NV21,w,h,null).compressToJpeg(android.graphics.Rect(0,0,w,h),(45+percent*50/100),jpegBuffer))
                            if(!closing){server?.frame=jpegBuffer.toByteArray();lastPublishedFrameAt=android.os.SystemClock.elapsedRealtime()}
                        }catch(error:Exception){if(!closing)android.util.Log.w("IPMsgCamera","Frame skipped",error)}finally{image.close();frameBusy=false}
                    }}catch(_:java.util.concurrent.RejectedExecutionException){image.close();frameBusy=false}
                },cameraHandler)
            }
            opening = true
            manager.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(device: CameraDevice) {
                    opening = false
                    if (closing) { device.close(); return }
                    camera = device
                    encoder.execute {
                        runCatching {
                            val local = LocalAddresses.forPeer(peerIp) ?: error("Connect Windows to the same Wi-Fi or hotspot, then refresh Nearby.")
                            val networkIdentity = LanRouting.identity(this@LiveCameraActivity,local) ?: error("Local network unavailable; refresh Nearby and try again.")
                            val identity = CameraTls.load(this@LiveCameraActivity,local)
                            cameraIdentity = identity; cameraNetworkIdentity = networkIdentity; cameraAddress = local
                            LiveCameraServer(local, peerIp, { handler.post { startRemoteRecording() } }, { handler.post { stopRemoteRecording() } }, { remoteStatus }, tls=identity.context,
                                snapshotTaken={cameraNotice("Receiver captured a photo")})
                        }.onSuccess { live -> handler.post {
                            if (closing) live.close() else {
                                server = live
                                runCatching { createSession(); snapshots.run(); transform(); message("Send the link when you’re ready") }.onFailure { fail(it) }
                            }
                        } }.onFailure { error -> handler.post { fail(error) } }
                    }
                }
                override fun onDisconnected(device: CameraDevice) { device.close(); fail(IllegalStateException("Camera disconnected")) }
                override fun onError(device: CameraDevice, error: Int) { device.close(); fail(IllegalStateException("Camera error $error")) }
            }, cameraHandler)
        }.onFailure { fail(it) }
    }

    @Suppress("DEPRECATION")
    private fun transform() {
        if(!preview.isAvailable || previewBox.width==0 || previewBox.height==0) return
        val rotation=windowManager.defaultDisplay.rotation
        frameRotation=(orientation-rotation*90+360)%360
        val portrait=frameRotation%180!=0
        val imageWidth=if(portrait)previewSize.height else previewSize.width;val imageHeight=if(portrait)previewSize.width else previewSize.height
        val scale=minOf(previewBox.width.toFloat()/imageWidth,previewBox.height.toFloat()/imageHeight)
        val w=(imageWidth*scale).toInt().coerceAtLeast(1);val h=(imageHeight*scale).toInt().coerceAtLeast(1)
        val params=preview.layoutParams as android.widget.FrameLayout.LayoutParams
        if(params.width!=w || params.height!=h){params.width=w;params.height=h;preview.layoutParams=params}
        // SurfaceTexture already handles the camera sensor's portrait rotation. Do not rotate it twice.
        val matrix=android.graphics.Matrix()
        if(rotation==Surface.ROTATION_90 || rotation==Surface.ROTATION_270){
            val view=android.graphics.RectF(0f,0f,w.toFloat(),h.toFloat())
            val buffer=android.graphics.RectF(0f,0f,previewSize.height.toFloat(),previewSize.width.toFloat())
            buffer.offset(view.centerX()-buffer.centerX(),view.centerY()-buffer.centerY())
            matrix.setRectToRect(view,buffer,android.graphics.Matrix.ScaleToFit.FILL)
            val fill=maxOf(h.toFloat()/previewSize.height,w.toFloat()/previewSize.width)
            matrix.postScale(fill,fill,view.centerX(),view.centerY());matrix.postRotate((90*(rotation-2)).toFloat(),view.centerX(),view.centerY())
        }else if(rotation==Surface.ROTATION_180)matrix.postRotate(180f,w/2f,h/2f)
        preview.setTransform(matrix)
    }

    @Suppress("DEPRECATION")
    private fun createSession() {
        if(Thread.currentThread()!=cameraThread) {cameraHandler.post{createSession()};return}
        val device=camera ?: return
        val targets=listOfNotNull(surface,frameReader?.surface)
        val generation=++epoch
        device.createCaptureSession(targets,object:CameraCaptureSession.StateCallback(){
            override fun onConfigured(configured:CameraCaptureSession){
                if(closing || generation!=epoch){configured.close();return}
                session=configured
                runCatching {
                    applyPreviewRequest()
                    updateFlashlightControl(true)
                    handler.post { if(!closing) uiReady=true }
                }.onFailure{fail(it)}
            }
            override fun onConfigureFailed(configured:CameraCaptureSession){configured.close();fail(IllegalStateException("Camera preview unavailable"))}
        },cameraHandler)
    }

    /** Runs on the camera thread; the owner is the only source of flashlight changes. */
    private fun applyPreviewRequest() {
        val device=camera ?: return
        val configured=session ?: return
        val request=device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW)
        listOfNotNull(surface,frameReader?.surface).forEach(request::addTarget)
        request.set(CaptureRequest.CONTROL_MODE,CameraMetadata.CONTROL_MODE_AUTO)
        request.set(CaptureRequest.CONTROL_AE_MODE,CaptureRequest.CONTROL_AE_MODE_ON)
        request.set(CaptureRequest.FLASH_MODE,if(flashlightAvailable && flashlightOn) CaptureRequest.FLASH_MODE_TORCH else CaptureRequest.FLASH_MODE_OFF)
        configured.setRepeatingRequest(request.build(),null,cameraHandler)
    }

    private fun toggleFlashlight() {
        if(closing) return
        cameraHandler.post {
            if(closing || !flashlightAvailable || camera==null || session==null) return@post
            flashlightOn=!flashlightOn
            runCatching { applyPreviewRequest() }.onSuccess { updateFlashlightControl(true) }.onFailure { error ->
                flashlightOn=false
                flashlightAvailable=false
                runCatching { applyPreviewRequest() }.onSuccess {
                    updateFlashlightControl(false)
                    message("Flashlight is unavailable on this camera")
                }.onFailure { fail(error) }
            }
        }
    }

    private fun updateFlashlightControl(ready:Boolean) {
        val available=flashlightAvailable
        val on=flashlightOn
        handler.post {
            if(isDestroyed) return@post
            uiFlashReady=ready && available && !closing
            uiFlashOn=on && uiFlashReady
        }
    }

    private fun toggleMicrophone() {
        if(microphoneOn) { stopMicrophone();message("Microphone off");return }
        if(closing || server==null || !uiReady) return
        confirm("Allow ${model.devices.label(peerIp,peerUser)} to hear AND record this phone’s microphone? Sound is sent only while this screen stays open. You can turn it off at any time.") {
            if(checkSelfPermission(Manifest.permission.RECORD_AUDIO)==PackageManager.PERMISSION_GRANTED) startMicrophone()
            else { permissionDialogOpen=true;microphonePermission.launch(Manifest.permission.RECORD_AUDIO) }
        }
    }

    private fun startMicrophone() {
        val live=server ?: return
        if(closing || microphoneOn || !lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) || getSystemService(android.app.KeyguardManager::class.java).isKeyguardLocked) return
        if(!model.devices.approved(peerIp,peerUser) || cameraNetworkChanged()) return
        val generation=++audioGeneration
        val capture=CameraAudio(this,{pcm->if(!closing && microphoneOn && generation==audioGeneration) server?.publishAudio(pcm)},{error->handler.post {
            if(generation==audioGeneration && !closing) {stopMicrophone();message("Microphone stopped. Check its permission or another recording app.");android.util.Log.w("IPMsgCamera","Microphone stopped",error)}
        }})
        audio=capture
        if(capture.start()) {
            microphoneOn=true;live.audioEnabled=true;uiMicOn=true;updateRemoteStatus()
            message("Microphone shared · receiver can hear and record")
        } else {
            capture.close();audio=null;live.stopAudio();message("Could not start microphone. Check Android’s microphone access.")
        }
    }

    private fun stopMicrophone() {
        ++audioGeneration
        microphoneOn=false
        runCatching { server?.stopAudio() }
        val capture=audio;audio=null;runCatching { capture?.close() }
        updateRemoteStatus()
        if(Looper.myLooper()==Looper.getMainLooper()) uiMicOn=false else handler.post{uiMicOn=false}
    }

    private fun updateRemoteStatus() {
        remoteStatus="${if(recording) "Recording · saved only by receiver" else "Live"} · ${if(microphoneOn) "microphone shared" else "microphone off"}"
    }

    private fun cameraNetworkChanged():Boolean {
        val address=cameraAddress ?: return false
        return LanRouting.identity(this,address)!=cameraNetworkIdentity
    }

    private val snapshots = object : Runnable {
        override fun run() {
            if (closing) return
            if(!Events.cameraAlertsEnabled(this@LiveCameraActivity)){message("Camera activity notifications are disabled");finish();return}
            val seconds = ((deadline - android.os.SystemClock.elapsedRealtime())/1000).coerceAtLeast(0)
            if(cameraNetworkChanged()) { message("Network changed"); shutdown(); finish(); return }
            if(!model.devices.approved(peerIp,peerUser)) { finish(); return }
            uiSeconds=seconds.toInt()
            uiRecording=recording
            uiNotice=if(android.os.SystemClock.elapsedRealtime()-viewerActionAt<8000)lastViewerAction else ""
            if(android.os.SystemClock.elapsedRealtime()>statusUntil) uiStatus=if(microphoneOn) "Sound shared · receiver can hear and record" else "Microphone off · only you can enable sound"
            handler.postDelayed(this,1000)
        }
    }

    private fun cameraNotice(text:String){
        lastViewerAction=text;viewerActionAt=android.os.SystemClock.elapsedRealtime()
        Events.cameraAction(this,"${model.devices.label(peerIp,peerUser)} · $text")
        message(if(recording) "● Recording · $text" else text)
    }
    private fun startRemoteRecording(){
        if(closing || recording)return
        recording=true;uiRecording=true;updateRemoteStatus()
        cameraNotice("Receiver started recording")
    }
    private fun stopRemoteRecording(){
        if(!recording)return
        recording=false;uiRecording=false;updateRemoteStatus()
        cameraNotice("Receiver stopped recording")
    }

    private fun shareLink() {
        val link = server?.url ?: return message("Camera is not ready")
        confirm("Send this temporary live camera link to $peerIp? The receiver can record and capture photos; captures download only to their browser. This phone receives activity alerts only. ${if(microphoneOn) "Microphone is currently shared; receiver can hear and record after enabling Sound." else "Microphone is off; only you can enable it."} Verify the HTTPS certificate fingerprint before trusting the link. Keep this screen open.") { share(null, "Live camera (10 minutes, owner-controlled): $link") }
    }
    private fun share(uri: Uri?, text: String = "Live camera recording") {
        val engine = model.engine ?: return message("Start listening before sharing")
        if (model.blocked(peerIp)) return message("Device removed")
        val action = { engine.sendMessage(peerIp, text, uri, allowPlain = !engine.encrypted(peerIp), expectedUser = peerUser) { queued -> handler.post { message(if (queued) "Shared with $peerIp" else "Could not send; check connection") } }; Unit }
        if (!engine.encrypted(peerIp)) confirm("This device cannot use encrypted messaging. Send without encryption?", action) else action()
    }
    private fun fail(error: Throwable) { if(closing) return; android.util.Log.w("IPMsgCamera", "Live camera stopped", error); model.error(error); shutdown(); handler.post{finish()} }
    private fun shutdown() {
        if (closing) return
        closing = true; ++epoch
        handler.removeCallbacksAndMessages(null)
        stopMicrophone()
        if(Looper.myLooper()==Looper.getMainLooper()) uiReady=false else handler.post{uiReady=false}
        networkCallback?.let {runCatching{getSystemService(android.net.ConnectivityManager::class.java).unregisterNetworkCallback(it)}};networkCallback=null
        server?.close(); server = null
        cameraHandler.post {
            recording=false
            flashlightOn=false
            // Request OFF before releasing the camera; closing the device also releases its torch.
            runCatching { applyPreviewRequest() }
            runCatching { session?.close() }; session=null
            runCatching { camera?.close() }; camera=null
            surface?.release();surface=null
            val reader=frameReader;frameReader=null;reader?.setOnImageAvailableListener(null,null)
            encoder.execute {reader?.close();encoder.shutdown();cameraThread.quitSafely()}
        }
    }
    override fun onPause() {
        super.onPause()
        // Permission prompts can pause the activity. Microphone access still ends immediately.
        stopMicrophone()
        if (!permissionDialogOpen && (camera != null || opening || server != null)) { shutdown(); finish() }
    }
    override fun onStop() { super.onStop();shutdown();if(!isFinishing)finish() }
    override fun onDestroy() { shutdown(); super.onDestroy() }
}

@Composable private fun CameraControl(kind:String,label:String,selected:Boolean,enabled:Boolean,modifier:Modifier=Modifier,onClick:()->Unit) {
    Surface(onClick=onClick,enabled=enabled,modifier=modifier.heightIn(min=56.dp).semantics{contentDescription=when(kind){"mic"->if(selected)"Turn microphone off" else "Share microphone, permission required";"flash"->if(!enabled)"Flashlight unavailable" else if(selected)"Turn flashlight off" else "Turn flashlight on";"quality"->"Stream quality $label";else->if(selected)"Close chat" else "Open chat while camera stays live"}},shape=RoundedCornerShape(18.dp),color=if(selected)MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerLow,contentColor=if(!enabled)MaterialTheme.colorScheme.onSurface.copy(alpha=0.38f) else if(selected)MaterialTheme.colorScheme.onPrimaryContainer else MaterialTheme.colorScheme.onSurface) {
        Column(Modifier.padding(vertical=8.dp),horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.spacedBy(4.dp)) {
            CameraSymbol(kind)
            Text(label,style=MaterialTheme.typography.labelSmall,maxLines=1)
        }
    }
}

@Composable private fun CameraSymbol(kind:String) {
    val color=LocalContentColor.current
    Canvas(Modifier.size(22.dp)) {
        val u=size.width/24f
        val stroke=1.7f*u
        fun line(x:Float,y:Float,x2:Float,y2:Float)=drawLine(color,Offset(x*u,y*u),Offset(x2*u,y2*u),stroke,StrokeCap.Round)
        when(kind) {
            "mic"->{drawRoundRect(color,Offset(9*u,2*u),androidx.compose.ui.geometry.Size(6*u,12*u),CornerRadius(3*u),style=Stroke(stroke));drawArc(color,0f,180f,false,Offset(6*u,8*u),androidx.compose.ui.geometry.Size(12*u,10*u),style=Stroke(stroke));line(6f,9f,6f,13f);line(18f,9f,18f,13f);line(12f,18f,12f,22f);line(8f,22f,16f,22f)}
            "flash"->{line(13f,2f,5f,14f);line(5f,14f,11f,14f);line(11f,14f,10f,22f);line(10f,22f,19f,10f);line(19f,10f,13f,10f);line(13f,10f,13f,2f)}
            "quality"->{line(3f,6f,21f,6f);line(3f,12f,21f,12f);line(3f,18f,21f,18f);drawCircle(color,2.4f*u,Offset(8*u,6*u));drawCircle(color,2.4f*u,Offset(16*u,12*u));drawCircle(color,2.4f*u,Offset(10*u,18*u))}
            "chat"->{drawRoundRect(color,Offset(3*u,3*u),androidx.compose.ui.geometry.Size(18*u,14*u),CornerRadius(4*u),style=Stroke(stroke));line(7f,17f,7f,21f);line(7f,21f,12f,17f);line(7f,8f,17f,8f);line(7f,12f,14f,12f)}
            else->{listOf(5f,12f,19f).forEach{drawCircle(color,1.7f*u,Offset(12*u,it*u))}}
        }
    }
}

