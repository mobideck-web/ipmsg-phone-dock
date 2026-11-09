package app.lantern.lan

import android.Manifest
import android.app.*
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow

data class Popup(val title: String, val text: String, val ip: String? = null, val user: String = "")

object Events {
    private val mutable = MutableSharedFlow<Popup>(extraBufferCapacity = 32)
    val popups = mutable.asSharedFlow()
    fun cameraAlertsEnabled(context:Context):Boolean {
        if(Build.VERSION.SDK_INT>=33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)return false
        val manager=context.getSystemService(NotificationManager::class.java)
        return manager.areNotificationsEnabled() && manager.getNotificationChannel("camera-actions-v1")?.importance!=NotificationManager.IMPORTANCE_NONE
    }
    fun cameraAction(context:Context,text:String){
        if(Build.VERSION.SDK_INT>=33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)return
        val manager=context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel("camera-actions-v1","Live camera activity",NotificationManager.IMPORTANCE_HIGH).apply{enableVibration(true)})
        manager.notify("camera",31,Notification.Builder(context,"camera-actions-v1").setSmallIcon(R.drawable.ic_ipmsg_notification)
            .setContentTitle("IPMsg · Live camera").setContentText(text).setCategory(Notification.CATEGORY_EVENT).setVisibility(Notification.VISIBILITY_PRIVATE).setAutoCancel(true).setOnlyAlertOnce(false).build())
    }
    fun popup(context: Context, title: String, text: String, ip: String? = null, user: String = "") {
        val model = Model.get(context)
        if (!model.setting("popups", true)) return
        val event = Popup(title, text, ip, user)
        if (model.state.value.foreground) { mutable.tryEmit(event); return }
        if (Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel("alerts-v4", "Pop-up alerts", NotificationManager.IMPORTANCE_HIGH).apply { enableVibration(true) })
        val route = Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        if (ip != null) route.putExtra("openIp", ip).putExtra("openUser", user).putExtra("openTab", "Chats")
        route.data=android.net.Uri.parse("ipmsg://chat/${android.net.Uri.encode(ip ?: "status")}/${android.net.Uri.encode(user)}")
        val destination = PendingIntent.getActivity(context, (ip ?: "status").hashCode(), route, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val builder = Notification.Builder(context, "alerts-v4")
        if(ip != null && user.isNotEmpty()) {
            val read=PendingIntent.getBroadcast(context,(ip+user+"read").hashCode(),Intent(context,NotificationActions::class.java).setAction("READ").setData(android.net.Uri.parse("ipmsg://read/${android.net.Uri.encode(ip)}/${android.net.Uri.encode(user)}")).putExtra("ip",ip).putExtra("user",user),PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            builder.addAction(Notification.Action.Builder(null,"Mark read",read).build())
            if(model.engine?.encrypted(ip)==true && model.devices.approved(ip,user)) {
                val reply=PendingIntent.getBroadcast(context,(ip+user+"reply").hashCode(),Intent(context,NotificationActions::class.java).setAction("REPLY").setData(android.net.Uri.parse("ipmsg://reply/${android.net.Uri.encode(ip)}/${android.net.Uri.encode(user)}")).putExtra("ip",ip).putExtra("user",user),PendingIntent.FLAG_UPDATE_CURRENT or (if(Build.VERSION.SDK_INT>=31) PendingIntent.FLAG_MUTABLE else 0))
                builder.addAction(Notification.Action.Builder(null,"Reply",reply).addRemoteInput(RemoteInput.Builder("reply").setLabel("Reply").build()).build())
            }
        }
        manager.notify(ip ?: "status", 2, builder.setSmallIcon(R.drawable.ic_ipmsg_notification)
            .setContentTitle(title).setContentText(text).setContentIntent(destination).setAutoCancel(true)
            .setVisibility(Notification.VISIBILITY_PRIVATE).setCategory(Notification.CATEGORY_MESSAGE).build())
    }
}
