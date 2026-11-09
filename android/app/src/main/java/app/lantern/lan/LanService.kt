package app.lantern.lan

import android.Manifest
import android.app.*
import android.content.Intent
import android.content.pm.PackageManager
import android.net.*
import android.net.wifi.WifiManager
import android.os.IBinder
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.distinctUntilChanged

class LanService : Service() {
    private lateinit var model: Model
    private var multicast: WifiManager.MulticastLock? = null
    private var callback: ConnectivityManager.NetworkCallback? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    override fun onCreate() {
        super.onCreate(); model = Model.get(this)
        getSystemService(NotificationManager::class.java).apply {
            createNotificationChannel(NotificationChannel("listening", "LAN listening", NotificationManager.IMPORTANCE_LOW))
            createNotificationChannel(NotificationChannel("messages", "Messages and files", NotificationManager.IMPORTANCE_DEFAULT))
        }
        scope.launch {
            model.state.map { it.listening }.distinctUntilChanged().collect { online ->
                if (online) Events.popup(this@LanService, "IPMsg", "Listening on your local network")
            }
        }
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == "STOP") { stopSelf(); return START_NOT_STICKY }
        if (model.engine != null) return START_NOT_STICKY
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop = PendingIntent.getService(this, 1, Intent(this, LanService::class.java).setAction("STOP"), PendingIntent.FLAG_IMMUTABLE)
        val notification = Notification.Builder(this, "listening").setSmallIcon(R.drawable.ic_ipmsg_notification)
            .setContentTitle("IPMsg").setContentText("Listening · no internet needed")
            .setContentIntent(open).setOngoing(true).addAction(Notification.Action.Builder(null, "Stop", stop).build()).build()
        startForeground(1, notification)
        multicast = (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager).createMulticastLock("lantern-discovery").apply { setReferenceCounted(false); acquire() }
        model.engine = LanEngine(model, { ip, notice ->
            val peer = model.state.value.peers.find { it.ip == ip }
            Events.popup(this, peer?.name ?: ip, if (model.setting("previews")) notice.text else if (notice.fileOffer) "New file" else "New message", ip, notice.user)
        }).also { engine ->
            engine.start().invokeOnCompletion {
                if (!model.state.value.listening) android.os.Handler(mainLooper).post { stopSelf() }
            }
        }
        callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) { model.changed(); model.engine?.refresh() }
            override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) { model.changed(); model.engine?.refresh() }
            override fun onLost(network: Network) { model.changed(); model.engine?.refresh() }
        }.also { getSystemService(ConnectivityManager::class.java).registerNetworkCallback(
            NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                .addTransportType(NetworkCapabilities.TRANSPORT_ETHERNET).build(), it) }
        // Phone-hosted hotspots are not exposed as a Network on every Android version.
        // Watch their local interface as well, without requiring cellular or internet access.
        scope.launch(Dispatchers.IO) {
            fun bindings() = LocalAddresses.list().associateWith { LanRouting.identity(this@LanService, it) }
            var previous = bindings()
            while (isActive) {
                delay(5_000)
                val current = bindings()
                if (current != previous) { previous = current; model.changed(); model.engine?.refresh() }
            }
        }
        return START_NOT_STICKY // Listening resumes only after the user enables it.
    }
    override fun onDestroy() {
        callback?.let { runCatching { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(it) } }
        model.engine?.close(); model.engine = null
        scope.cancel()
        multicast?.let { if (it.isHeld) it.release() }
        stopForeground(STOP_FOREGROUND_REMOVE); super.onDestroy()
    }
    override fun onBind(intent: Intent?): IBinder? = null
}
