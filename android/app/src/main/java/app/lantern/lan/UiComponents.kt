package app.lantern.lan

import android.net.Uri
import android.os.StatFs
import android.provider.Settings
import android.content.Intent
import androidx.compose.foundation.Image
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@Composable fun ErrorNotice(message: String, onDismiss: () -> Unit, onSecurity: () -> Unit = {}) {
    var details by remember(message) { mutableStateOf(false) }
    val securityRule = message.contains("Secure file requests required", true) || message.contains("Encrypted messages only", true) || message.contains("Secure messaging unavailable", true) || message.contains("Require encrypted files", true)
    val friendly = when {
        listOf("ENETUNREACH", "EHOSTUNREACH", "Network is unreachable", "No route to host").any { message.contains(it, true) } ->
            "Device is out of reach" to "Connect the PC to your current Wi-Fi or phone hotspot, then refresh Nearby. Mobile data is not needed."
        message.contains("ECONNREFUSED", true) || message.contains("Connection refused", true) ->
            "The PC isn’t accepting connections" to "Keep IP Messenger running on Windows and allow it through Windows Firewall on this local network."
        message.contains("Secure file requests required", true) ->
            "Windows requested an unencrypted file" to "The download was blocked by your encrypted-file setting. Encrypted messages and encrypted file contents use separate protocol features. Check Security in Settings."
        message.contains("Encrypted messages only", true) || message.contains("Secure messaging unavailable", true) ->
            "Encrypted messaging is unavailable" to "Refresh Nearby to exchange device keys. Your message was blocked by the encrypted-messages setting; nothing was sent."
        else -> "Couldn’t complete this action" to message
    }
    Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.errorContainer)) {
        Column(Modifier.padding(horizontal = 16.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(friendly.first, style = MaterialTheme.typography.titleSmall)
            Text(friendly.second, style = MaterialTheme.typography.bodySmall)
            if (details && friendly.second != message) SelectionContainer { Text(message, style = MaterialTheme.typography.labelSmall) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                if (securityRule) TextButton(onClick = onSecurity) { Text("Security settings") }
                if (friendly.second != message) TextButton(onClick = { details = !details }) { Text(if (details) "Hide details" else "Details") }
                TextButton(onClick = onDismiss) { Text("Dismiss") }
            }
        }
    }
}

@Composable fun ChatText(value: String) {
    val text = androidx.compose.ui.text.buildAnnotatedString {
        append(value)
        Regex("https?://[^\\s<>]+", RegexOption.IGNORE_CASE).findAll(value).forEach { match ->
            val url = match.value.trimEnd('.', ',', ';', ')')
            addLink(androidx.compose.ui.text.LinkAnnotation.Url(url), match.range.first, match.range.first + url.length)
        }
    }
    Text(text, style = MaterialTheme.typography.bodyLarge, lineHeight = 24.sp)
}

fun fileSize(bytes: Long): String = when {
    bytes >= 1073741824L -> "%.1f GB".format(bytes / 1073741824.0)
    bytes >= 1048576L -> "%.1f MB".format(bytes / 1048576.0)
    bytes >= 1024 -> "%.1f KB".format(bytes / 1024.0)
    else -> "$bytes B"
}

@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable fun FileCard(model: Model, t: Transfer, accept: (Transfer) -> Unit, onSettings:()->Unit = {}) {
    val state by model.state.collectAsState()
    val uri = t.savedUri ?: t.sourceUri
    val active = t.active
    var restart by remember {mutableStateOf(false)}
    val label = when {
        t.phase == TransferPhase.OFFERED -> if (t.outgoing) "Shared" else "Ready"
        t.phase == TransferPhase.FAILED -> "Failed"
        t.phase == TransferPhase.RECEIVING -> "Downloading"
        else -> t.status
    }
    val thumbnail by produceState<ImageBitmap?>(null, uri, t.status) {
        if (uri != null && t.file.name.substringAfterLast('.', "").lowercase() in setOf("jpg", "jpeg", "png", "webp")) {
            value = withContext(Dispatchers.IO) { runCatching { model.context.contentResolver.loadThumbnail(Uri.parse(uri), android.util.Size(320, 240), null).asImageBitmap() }.getOrNull() }
        }
    }
    Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
            thumbnail?.let { Image(it, t.file.name, Modifier.fillMaxWidth().heightIn(max = 180.dp), contentScale = ContentScale.Fit) }
            Text(t.file.name, fontWeight = FontWeight.SemiBold, maxLines = 2)
            Text("${if (t.outgoing) "↑" else "↓"} ${fileSize(t.file.size)} · $label", style = MaterialTheme.typography.labelSmall)
            if (active) {
                LinearProgressIndicator(progress = { if (t.file.size == 0L) 1f else (t.done.toDouble() / t.file.size).toFloat().coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
                val percent=if(t.file.size==0L) 100 else (t.done.toDouble()/t.file.size*100).toInt().coerceIn(0,100)
                val eta=if(t.speed>0) " · ~${((t.file.size-t.done).coerceAtLeast(0)/t.speed)}s left" else ""
                Text("$percent% · ${fileSize(t.done)} / ${fileSize(t.file.size)}" + if(t.speed>0) " · ${fileSize(t.speed)}/s$eta" else "", style = MaterialTheme.typography.labelSmall)
            }
            if(t.phase==TransferPhase.EXPIRED)Text("Ask the sender to share this file again.",style=MaterialTheme.typography.bodySmall)
            if (t.phase == TransferPhase.FAILED || t.phase==TransferPhase.INTERRUPTED) {
                Text(TransferRecovery.message(t),style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
                if(t.outgoing)Text(if(model.engine?.retryAvailable(t)==true)"Offer still available · retry from the receiving PC" else "Offer unavailable · share the source file again",style=MaterialTheme.typography.labelSmall)
            }
            if(TransferRecovery.canRestart(t) && state.peers.none{it.ip==t.ip && it.user==t.peerUser})Text("Waiting for sender · refresh Nearby",style=MaterialTheme.typography.labelSmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                if (!t.outgoing && t.phase == TransferPhase.OFFERED) {
                    TextButton(onClick = { accept(t) }, enabled = state.listening) { Text("Save") }
                    TextButton(onClick = { model.transfer(t.copy(phase = TransferPhase.DECLINED)) }) { Text("Decline") }
                }
                if (TransferRecovery.canRestart(t)) TextButton(onClick = { restart=true }, enabled = state.listening && state.peers.any{it.ip==t.ip && it.user==t.peerUser}) { Text("Restart download") }
                if(t.phase==TransferPhase.FAILED && t.failure in setOf(FailureReason.PERMISSION,FailureReason.STORAGE))TextButton(onClick=onSettings){Text("Storage settings")}
                if(t.phase==TransferPhase.FAILED && t.failure==FailureReason.SECURITY)TextButton(onClick=onSettings){Text("Security settings")}
                if (active || (t.outgoing && (t.phase == TransferPhase.OFFERED || model.engine?.retryAvailable(t)==true))) TextButton(onClick = { model.engine?.cancel(t.id) }) { Text("Cancel") }
                if(t.batchId != null && (active || t.phase == TransferPhase.OFFERED)) TextButton(onClick={model.engine?.cancelBatch(t.batchId)}){Text("Cancel batch")}
                if (uri != null && (t.outgoing || t.phase == TransferPhase.SAVED)) {
                    TextButton(onClick = { FileActions.open(model, uri, t.file.name) }) { Text("Open") }
                    TextButton(onClick = { FileActions.folder(model, uri) }) { Text("Folder") }
                }
            }
        }
    }
    if(restart)AlertDialog(onDismissRequest={restart=false},title={Text("Restart download?")},text={Text("Download ${t.file.name} again from zero? The incomplete copy was removed. Keep the sender online; an expired offer must be shared again.")},confirmButton={TextButton(onClick={restart=false;accept(t.copy(done=0,speed=0,failure=null,detail=null))}){Text("Restart")}},dismissButton={TextButton(onClick={restart=false}){Text("Cancel")}})
}

@Composable private fun Preference(model: Model, title: String, key: String, default: Boolean = false, note: String? = null) {
    val state by model.state.collectAsState()
    val checked = remember(state.revision, key, default) { model.setting(key, default) }
    Row(Modifier.fillMaxWidth().toggleable(value = checked, role = Role.Switch, onValueChange = { model.setSetting(key, it) }).padding(vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) { Text(title, style = MaterialTheme.typography.bodyLarge); if (note != null) Text(note, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        Switch(checked = checked, onCheckedChange = null)
    }
}

@Composable fun PreferencesScreen(model: Model, onClear: () -> Unit, onReset: () -> Unit, onNotices: () -> Unit, onStorage: () -> Unit) {
    val state by model.state.collectAsState()
    var name by remember { mutableStateOf(model.name) }
    var manageStorage by remember { mutableStateOf(false) }
    if(manageStorage){StorageScreen(model,onBack={manageStorage=false});return}
    val folderPicker = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) runCatching {
            model.context.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            model.preferences.edit().putString("download-folder", uri.toString()).apply()
            model.update { it.copy(revision = it.revision + 1) }
        }.onFailure(model::error)
    }
    var details by remember { mutableStateOf(false) }
    LazyColumn(verticalArrangement = Arrangement.spacedBy(18.dp), contentPadding = PaddingValues(bottom = 20.dp)) {
        item { Text("Settings",style=MaterialTheme.typography.headlineMedium) }
        item { SettingsSection { Text("Your device", style = MaterialTheme.typography.titleMedium)
            OutlinedTextField(name, { name = it.take(40) }, label = { Text("Display name") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            if (name.trim() != model.name) FilledTonalButton(onClick = { model.name = name; name = model.name; model.engine?.refresh() }) { Text("Save name") }
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
            Text("My IP", style = MaterialTheme.typography.labelLarge)
            val ips = remember(state.revision, state.listening) { LocalAddresses.list() }
            SelectionContainer { Text(ips.joinToString("\n").ifBlank { "No local network address" }) }
            Text("Local use only · Internet and mobile data are not required.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        } }
        item { SettingsSection { Text("Notifications", style = MaterialTheme.typography.titleMedium)
 Preference(model, "Pop-up alerts", "popups", true, "Messages and file activity")
 Preference(model, "Device connections", "device-alerts", true, "Alert when a device appears")
 Preference(model, "Message previews", "previews", note = "Show message text in notifications")
 TextButton(onClick = { model.context.startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, model.context.packageName).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }) { Text("Android notification settings") } } }
        item { SettingsSection { Text("Storage", style = MaterialTheme.typography.titleMedium)

            val space = remember { StatFs(model.context.filesDir.absolutePath) }
            Text("${fileSize(space.availableBytes)} free of ${fileSize(space.totalBytes)}", style = MaterialTheme.typography.bodyMedium)
            Text("Downloads: " + DownloadStorage.label(model), style = MaterialTheme.typography.bodyMedium)
            Row { TextButton(onClick = { folderPicker.launch(null) }) { Text("Change folder") }
                TextButton(onClick = { model.preferences.edit().remove("download-folder").apply(); model.update { it.copy(revision = it.revision + 1) } }) { Text("Use default") } }
            TextButton(onClick={manageStorage=true}) { Text("Manage storage") }
            TextButton(onClick = onStorage) { Text("Browse storage") }
        } }
        item { SettingsSection {
            Text("Security",style=MaterialTheme.typography.titleMedium)
            Text("Encryption is used when compatible. These switches add strict blocking rules.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Preference(model,"Encrypted messages only","strict-messages",note="Block unencrypted messages sent and received, including file offers.")
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
            Preference(model,"Require encrypted files","strict-files",note="For files you send. Block Windows downloads without file encryption; incoming files are unaffected.")
            TextButton(onClick={details=!details}){Text(if(details)"Hide explanation" else "How these settings work")}
            if(details){
                Text("Messages: turning the first switch off still uses encryption when available. Sending plaintext needs your confirmation. An approved secure device never silently falls back to plaintext.",style=MaterialTheme.typography.bodySmall)
                Text("Files sent: Windows chooses how it downloads each file. The second switch rejects requests for unencrypted file contents. A PC may support encrypted chat but not encrypted downloads, so screenshots and other files can be blocked. Turning it off allows that compatibility mode; it does not turn encryption off.",style=MaterialTheme.typography.bodySmall)
                Text("Files received have their own plaintext confirmation. Live camera always uses HTTPS. Classic IPMsg file encryption does not authenticate file contents.",style=MaterialTheme.typography.bodySmall)
                Text("Device fingerprints", style = MaterialTheme.typography.labelLarge)
                state.peers.filter{model.engine?.fingerprint(it.ip)!=null}.forEach{peer->SelectionContainer{Text("${peer.name}\n${model.engine?.fingerprint(peer.ip)}",style=MaterialTheme.typography.bodySmall)}}
            }
        } }
        item { SettingsSection { Text("History", style = MaterialTheme.typography.titleMedium); TextButton(onClick = onClear) { Text("Clear history") } } }
        item { SettingsSection { Text("Connection", style = MaterialTheme.typography.titleMedium); Text(if (state.listening) "Listening on this network" else "Listening paused", style = MaterialTheme.typography.bodyMedium)
            Text("Use the same Wi-Fi or connect the PC to this phone’s hotspot. After switching networks, refresh Nearby and check the PC’s current IP address.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Diagnostics(model)
        } }
        item { SettingsSection { Text("IPMsg " + remember { model.context.packageManager.getPackageInfo(model.context.packageName,0).versionName } + " · Unofficial Android client", style = MaterialTheme.typography.labelSmall); TextButton(onClick = onNotices) { Text("Licenses & credits") } } }
    }
}

@Composable private fun SettingsSection(content:@Composable ColumnScope.()->Unit) {
    Card(Modifier.fillMaxWidth(),colors=CardDefaults.cardColors(containerColor=MaterialTheme.colorScheme.surfaceContainerLow)) {
        Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(12.dp),content=content)
    }
}
