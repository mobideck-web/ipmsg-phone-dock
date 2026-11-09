package app.lantern.lan

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import android.net.Uri

@Composable fun DeviceDialog(model: Model, peer: Peer, close: () -> Unit, connected: () -> Unit) {
    val state by model.state.collectAsState()
    val saved = remember(state.revision,peer.ip) { model.devices.record(peer.ip,peer.user) }
    var nickname by remember { mutableStateOf(saved?.nickname.orEmpty()) }
    var favorite by remember { mutableStateOf(saved?.favorite ?: false) }
    AlertDialog(onDismissRequest=close,title={ Text("Connect to ${model.devices.label(peer)}?") },text={ Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text(peer.ip)
        SelectionContainer { Text(model.devices.fingerprint(peer.ip)?.chunked(4)?.joinToString(" ") ?: "Unverified legacy device. Plaintext sharing needs confirmation.",style=MaterialTheme.typography.bodySmall) }
        OutlinedTextField(nickname,{nickname=it.take(40)},label={Text("Local nickname")},singleLine=true)
        Row { Checkbox(favorite,{favorite=it}); Text("Favorite") }
        Text("Compare the key fingerprint with the intended device. Files and camera sharing need separate permission.",style=MaterialTheme.typography.bodySmall)
    } },confirmButton={ TextButton(onClick={ runCatching { model.devices.approve(peer); model.devices.record(peer.ip,peer.user)?.let { model.repository.save(it.copy(nickname=nickname,favorite=favorite)) }; connected() }.onFailure(model::error) }) { Text("Connect") } },dismissButton={TextButton(onClick=close){Text("Cancel")}})
}

@Composable fun OutboxPanel(model: Model, onReview: (OutboxItem) -> Unit) {
    val state by model.state.collectAsState()
    val rows = remember(state.revision) { model.repository.outbox() }
    if(rows.isEmpty()) return
    Text("Pending items",style=MaterialTheme.typography.titleMedium)
    Column(Modifier.heightIn(max=240.dp).verticalScroll(androidx.compose.foundation.rememberScrollState())) { rows.forEach { item ->
        Row(Modifier.fillMaxWidth()) {
            Column(Modifier.weight(1f)) { Text(item.text.ifBlank { "${item.uris.size} files" }.take(70)); Text("${item.ip} · ${if(System.currentTimeMillis()>item.expires) "Expired" else if(item.state=="SENDING") "Sending" else "Review before sending"}",style=MaterialTheme.typography.bodySmall) }
            TextButton(onClick={onReview(item)},enabled=item.state!="SENDING" && System.currentTimeMillis()<=item.expires) {Text("Review")}
            TextButton(onClick={model.repository.removeOutbox(item.id)},enabled=item.state!="SENDING") {Text("Remove")}
        }
    } }
}

@Composable fun KnownDevicesSettings(model: Model) {
    val state by model.state.collectAsState()
    val rows = remember(state.revision) { model.repository.devices() }
    var edit by remember { mutableStateOf<KnownDevice?>(null) }
    rows.forEach { device ->
        Row(Modifier.fillMaxWidth()) { Column(Modifier.weight(1f)) { Text((if(device.favorite) "★ " else "") + device.nickname.ifBlank { device.name }); Text("${device.ip} · ${if(device.blocked) "Blocked" else if(!device.approved) "Approval needed" else if(device.fingerprint != null) "Key approved" else "Unverified"}",style=MaterialTheme.typography.bodySmall) }; TextButton(onClick={edit=device}) {Text("Manage")} }
    }
    edit?.let { device ->
        var nickname by remember(device.id) {mutableStateOf(device.nickname)}
        AlertDialog(onDismissRequest={edit=null},title={Text("Device settings")},text={Column {
            OutlinedTextField(nickname,{nickname=it.take(40)},label={Text("Local nickname")})
            TextButton(onClick={model.repository.save(device.copy(favorite=!device.favorite));edit=null}) {Text(if(device.favorite) "Remove favorite" else "Favorite")}
            SelectionContainer { Text(device.fingerprint?.chunked(4)?.joinToString(" ") ?: "Unverified legacy device",style=MaterialTheme.typography.bodySmall) }
            TextButton(onClick={model.devices.revoke(device);edit=null}) {Text("Revoke approval")}
            TextButton(onClick={model.devices.revoke(device,!device.blocked); if(device.blocked) model.restore(device.ip);edit=null}) {Text(if(device.blocked) "Unblock" else "Block device")}
            TextButton(onClick={model.engine?.resetTrust(device.ip,device.user);edit=null}) {Text("Reset this device key")}
        }},confirmButton={TextButton(onClick={model.repository.save(device.copy(nickname=nickname));edit=null}){Text("Save")}},dismissButton={TextButton(onClick={edit=null}){Text("Cancel")}})
    }
}

@Composable fun Diagnostics(model: Model) {
    val state by model.state.collectAsState()
    var show by remember {mutableStateOf(false)}
    TextButton(onClick={show=!show}) {Text(if(show) "Hide troubleshooting" else "Troubleshoot connection")}
    if(show) Text("${if(state.listening) "Listening on UDP/TCP 2425" else "Listening is off"}\nLocal IP: ${LocalAddresses.list().joinToString().ifBlank { "No network" }}\nNotifications: ${if(model.context.getSystemService(android.app.NotificationManager::class.java).areNotificationsEnabled()) "Allowed" else "Disabled in Android settings"}\nUse the same local network. Allow Windows IP Messenger through Private-network Firewall. Guest Wi-Fi, VPN routing, and access-point isolation may prevent discovery. Try Refresh or add the PC's IP. Keep the app's ongoing notification enabled while receiving.",style=MaterialTheme.typography.bodySmall)
}
