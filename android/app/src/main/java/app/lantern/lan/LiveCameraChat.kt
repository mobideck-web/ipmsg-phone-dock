package app.lantern.lan

import android.app.NotificationManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.ContextWrapper
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.LifecycleOwner
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** The real source conversation, kept in the camera Activity so sharing stays in the foreground. */
@Composable
fun LiveCameraChat(model: Model, peerIp: String, peerUser: String, modifier: Modifier = Modifier) {
    val state by model.state.collectAsState()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val list = rememberLazyListState()
    val draftKey = "camera-chat-draft:$peerIp|$peerUser"
    var draft by rememberSaveable(peerIp, peerUser) { mutableStateOf(model.preferences.getString(draftKey, "").orEmpty()) }
    LaunchedEffect(draft, draftKey) {
        model.preferences.edit().apply { if (draft.isEmpty()) remove(draftKey) else putString(draftKey, draft) }.apply()
    }
    var error by remember(peerIp, peerUser) { mutableStateOf<String?>(null) }
    var plainText by remember(peerIp, peerUser) { mutableStateOf<String?>(null) }
    var sending by remember(peerIp, peerUser) { mutableStateOf(false) }
    var action by remember(peerIp, peerUser) { mutableStateOf<Chat?>(null) }
    var loaded by remember(peerIp, peerUser) { mutableStateOf<List<Chat>>(emptyList()) }
    val owner = remember(context) { context.chatLifecycleOwner() }
    var resumed by remember(owner) { mutableStateOf(owner?.lifecycle?.currentState?.isAtLeast(Lifecycle.State.RESUMED) == true) }
    DisposableEffect(owner) {
        val observer = LifecycleEventObserver { source, _ -> resumed = source.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
        owner?.lifecycle?.addObserver(observer)
        onDispose { owner?.lifecycle?.removeObserver(observer) }
    }
    LaunchedEffect(peerIp, peerUser, state.chats, state.revision) {
        loaded = withContext(Dispatchers.IO) { model.repository.search("", peerIp, peerUser, limit = 100) }
    }
    val chats = remember(loaded, state.chats, peerIp, peerUser) {
        (state.chats.filter { it.ip == peerIp && it.peerUser == peerUser }.takeLast(100) + loaded).distinctBy { it.id }.sortedBy { it.id }.takeLast(100)
    }
    var initialScroll by remember(peerIp, peerUser) { mutableStateOf(true) }
    LaunchedEffect(chats.lastOrNull()?.id) {
        if (chats.isNotEmpty()) {
            if (initialScroll) { list.scrollToItem(chats.lastIndex); initialScroll = false }
            else if (chats.last().outgoing || list.firstVisibleItemIndex >= chats.lastIndex - 4) list.animateScrollToItem(chats.lastIndex)
        }
    }
    LaunchedEffect(resumed, chats) {
        if (resumed && chats.any { !it.outgoing && !it.seen }) {
            withContext(Dispatchers.IO) { model.markSeen(peerIp, peerUser) }
            context.getSystemService(NotificationManager::class.java).cancel(peerIp, 2)
        }
    }
    val peer = state.peers.find { it.ip == peerIp && it.user == peerUser }
    val online = state.listening && model.engine != null && peer != null
    val approved = model.devices.approved(peerIp, peerUser)
    val encrypted = model.engine?.encrypted(peerIp) == true
    val needsKey = !encrypted && (model.setting("strict-messages") || model.devices.record(peerIp, peerUser)?.fingerprint != null)
    val unavailable = when {
        !state.listening || model.engine == null -> "Listening is paused. Keep your draft and enable listening after the camera ends."
        peer == null -> "Device offline. Your draft stays here; nothing is sent automatically."
        !approved -> "Device approval changed. Reconnect from Nearby after the camera ends."
        needsKey -> "The device’s encryption key is unavailable. Refresh Nearby after the camera ends."
        else -> null
    }
    fun send(text: String, allowPlain: Boolean) {
        val current = model.state.value
        val engine = model.engine
        if (!current.listening || engine == null || current.peers.none { it.ip == peerIp && it.user == peerUser }) {
            error = "This device is offline. Your draft has been kept."
            return
        }
        runCatching { model.devices.authorize(peerIp, peerUser, engine.encrypted(peerIp), allowPlain) }
            .onFailure { error = it.message ?: "Verify this device before sending." }
            .onSuccess {
                sending = true
                engine.sendMessage(peerIp, text, allowPlain = allowPlain, expectedUser = peerUser) { queued ->
                    // The engine can report a later send failure after accepting the local item.
                    // Preserve unsent text even if the chat panel was closed before this callback.
                    val stored = model.preferences.getString(draftKey, "").orEmpty()
                    if (queued && stored == text) model.preferences.edit().remove(draftKey).apply()
                    else if (!queued && stored.isBlank()) model.preferences.edit().putString(draftKey, text).apply()
                    scope.launch(Dispatchers.Main) {
                        sending = false
                        if (queued) { if (draft == text) draft = ""; error = null }
                        else {
                            if (draft.isBlank()) draft = text
                            error = "Message could not be sent. Check this conversation before trying again."
                        }
                    }
                }
            }
    }
    Column(modifier, verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("Chat", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
            Text(if (online) "● ${model.devices.label(peerIp, peerUser)}" else "Offline", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, modifier = Modifier.widthIn(max = 220.dp))
        }
        if (unavailable != null) Text(unavailable, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (error != null) {
            Surface(color = MaterialTheme.colorScheme.errorContainer, shape = MaterialTheme.shapes.medium) {
                Row(Modifier.padding(start = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(error.orEmpty(), style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                    TextButton(onClick = { error = null }) { Text("Dismiss") }
                }
            }
        }
        LazyColumn(Modifier.weight(1f).fillMaxWidth(), state = list, contentPadding = PaddingValues(vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            if (chats.isEmpty()) item { Text("Messages stay in this conversation.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            items(chats, key = { it.id }) { chat ->
                ChatMessageBubble(chat, onLongClick = { action = chat }) {
                    Text(chat.text.ifBlank { "Shared a file" }, style = MaterialTheme.typography.bodyMedium)
                }
            }
        }
        ChatComposer(draft, { draft = it }, {
            val text = draft
            if (model.engine?.encrypted(peerIp) == true) send(text, false)
            else plainText = text
        }, sendEnabled = online && approved && !needsKey, sending = sending)
    }
    plainText?.let { text ->
        AlertDialog(
            onDismissRequest = { plainText = null },
            title = { Text("Send without encryption?") },
            text = { Text("${model.devices.label(peerIp, peerUser)} has no compatible encryption key. This message can be read by others on this network.\n\n${text.take(400)}") },
            confirmButton = { TextButton(onClick = { plainText = null; send(text, true) }) { Text("Send once") } },
            dismissButton = { TextButton(onClick = { plainText = null }) { Text("Cancel") } }
        )
    }
    action?.let { chat ->
        AlertDialog(onDismissRequest = { action = null }, title = { Text("Message") }, text = {
            Column {
                TextButton(onClick = { context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("IPMsg", chat.text)); action = null }) { Text("Copy text") }
                TextButton(onClick = {
                    val quote = "> ${chat.text.take(1000).replace("\n", "\n> ")}\n"
                    val quoted = quote + draft
                    if (quoted.toByteArray(Charsets.UTF_8).size <= 6000) draft = quoted else error = "Shorten your draft before adding a quote."
                    action = null
                }) { Text("Quote reply") }
            }
        }, confirmButton = { TextButton(onClick = { action = null }) { Text("Done") } })
    }
}

private fun Context.chatLifecycleOwner(): LifecycleOwner? = when (this) {
    is LifecycleOwner -> this
    is ContextWrapper -> if (baseContext === this) null else baseContext.chatLifecycleOwner()
    else -> null
}
