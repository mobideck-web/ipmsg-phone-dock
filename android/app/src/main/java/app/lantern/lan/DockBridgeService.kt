package app.lantern.lan

import android.app.*
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.IBinder
import android.os.Handler

class DockBridgeService:Service() {
    private var owned:DockSession?=null
    private var latestStart=0
    private val handler=Handler(android.os.Looper.getMainLooper())
    private val watch=object:Runnable {
        override fun run() {
            if(DockSessions.current()==null) stopSelfResult(latestStart)
            else handler.postDelayed(this,1000)
        }
    }
    private val locked=object:BroadcastReceiver() {
        override fun onReceive(context:Context,intent:Intent) {
            DockSessions.stop("Phone Dock stopped when the screen turned off."); stopSelfResult(latestStart)
        }
    }
    override fun onCreate() {
        super.onCreate()
        registerReceiver(locked,IntentFilter(Intent.ACTION_SCREEN_OFF))
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel("dock-files","Phone Dock files",NotificationManager.IMPORTANCE_LOW))
    }
    override fun onStartCommand(intent:Intent?,flags:Int,startId:Int):Int {
        latestStart=startId
        if(intent?.action=="STOP") { DockSessions.stop(); stopSelfResult(startId); return START_NOT_STICKY }
        val open=PendingIntent.getActivity(this,2410,Intent(this,DockFilesActivity::class.java),PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop=PendingIntent.getService(this,2411,Intent(this,DockBridgeService::class.java).setAction("STOP"),PendingIntent.FLAG_IMMUTABLE)
        startForeground(2428,Notification.Builder(this,"dock-files").setSmallIcon(R.drawable.ic_ipmsg_notification)
            .setContentTitle("IPMsg Phone Dock").setContentText("Only selected files • local PC connection")
            .setContentIntent(open).setOngoing(true).addAction(Notification.Action.Builder(null,"Stop",stop).build()).build())
        val requested=intent?.getStringExtra("dock_session"); intent?.removeExtra("dock_session")
        if(requested!=null) {
            if(!DockPolicy.token(requested) || !DockSessions.open(this,requested)) stopSelfResult(startId)
            else owned=DockSessions.current()
        } else if(!DockSessions.active()) stopSelfResult(startId)
        handler.removeCallbacks(watch);handler.postDelayed(watch,1000)
        return START_NOT_STICKY
    }
    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null);runCatching {unregisterReceiver(locked)}
        owned?.let{DockSessions.stopIfCurrent(it)}; stopForeground(STOP_FOREGROUND_REMOVE); super.onDestroy()
    }
    override fun onBind(intent:Intent?):IBinder?=null
}
