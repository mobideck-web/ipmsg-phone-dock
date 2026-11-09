package app.lantern.lan

import android.app.*
import android.content.*
import kotlinx.coroutines.*

class NotificationActions : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val pending = goAsync()
        CoroutineScope(SupervisorJob()+Dispatchers.IO).launch {
            try {
                val model = Model.get(context); val ip=intent.getStringExtra("ip") ?: return@launch; val user=intent.getStringExtra("user").orEmpty()
                if(intent.action=="READ") { model.markSeen(ip,user); context.getSystemService(NotificationManager::class.java).cancel(ip,2); return@launch }
                val text = RemoteInput.getResultsFromIntent(intent)?.getCharSequence("reply")?.toString()?.takeIf { it.isNotBlank() } ?: return@launch
                model.preferences.edit().putString("reply-draft:$ip|$user",text).apply()
                val engine = model.engine
                var deliveredToEngine=false
                if(engine != null && engine.encrypted(ip) && model.devices.approved(ip,user)) {
                    val accepted=CompletableDeferred<Boolean>();val job=engine.sendMessage(ip,text,expectedUser=user,onQueued={accepted.complete(it)});val queued=withTimeoutOrNull(8000){accepted.await()}==true;if(!queued) job.cancel()
                    if(queued) {deliveredToEngine=true; model.preferences.edit().remove("reply-draft:$ip|$user").apply();model.markSeen(ip,user);context.getSystemService(NotificationManager::class.java).cancel(ip,2)}
                }
                if(!deliveredToEngine) {
                    // No background plaintext downgrade. A notification tap opens the draft for review.
                    val route=Intent(context,MainActivity::class.java).setData(android.net.Uri.parse("ipmsg://reply/${android.net.Uri.encode(ip)}/${android.net.Uri.encode(user)}")).putExtra("openIp",ip).putExtra("openUser",user).putExtra("replyDraft",text)
                    val tap=PendingIntent.getActivity(context,(ip+user+"review").hashCode(),route,PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
                    context.getSystemService(NotificationManager::class.java).notify(ip,3,Notification.Builder(context,"alerts-v4").setSmallIcon(R.drawable.ic_ipmsg_notification).setContentTitle("Review reply in IPMsg").setContentText("Device approval or connection needs attention").setContentIntent(tap).setAutoCancel(true).setVisibility(Notification.VISIBILITY_PRIVATE).build())
                }
            } finally { pending.finish() }
        }
    }
}
