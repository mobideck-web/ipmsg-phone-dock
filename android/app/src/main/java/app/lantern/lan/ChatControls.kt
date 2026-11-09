package app.lantern.lan

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import java.text.DateFormat
import java.util.Date

/** One composer shared by the conversation and the camera's inline chat. */
@Composable
fun ChatComposer(
    value: String,
    onValueChange: (String) -> Unit,
    onSend: () -> Unit,
    modifier: Modifier = Modifier,
    onAttach: (() -> Unit)? = null,
    enabled: Boolean = true,
    sendEnabled: Boolean = true,
    sending: Boolean = false,
    queue: Boolean = false
) {
    var inputNotice by remember { mutableStateOf<String?>(null) }
    val bytes = value.toByteArray(Charsets.UTF_8).size
    val valid = '\u0000' !in value && bytes <= 6000
    Surface(modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surface, tonalElevation = 0.dp) {
        Column {
        Row(Modifier.padding(vertical = 8.dp), verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (onAttach != null) {
                FilledTonalIconButton(onClick = onAttach, modifier = Modifier.size(48.dp), enabled = enabled) {
                    Text("+", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.semantics { contentDescription = "Attach or share" })
                }
            }
            OutlinedTextField(
                value = value,
                onValueChange = { next ->
                    inputNotice = when {
                        '\u0000' in next -> "This text contains an unsupported character."
                        next.toByteArray(Charsets.UTF_8).size > 6000 -> "Message is too long. The limit is 6,000 bytes."
                        else -> null
                    }
                    if (inputNotice == null) onValueChange(next)
                },
                modifier = Modifier.weight(1f),
                placeholder = { Text("Message") },
                enabled = enabled,
                maxLines = 4,
                shape = RoundedCornerShape(24.dp),
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Default),
                colors = OutlinedTextFieldDefaults.colors(
                    unfocusedContainerColor = MaterialTheme.colorScheme.surfaceContainerLow,
                    focusedContainerColor = MaterialTheme.colorScheme.surfaceContainerLow,
                    unfocusedBorderColor = MaterialTheme.colorScheme.outlineVariant
                )
            )
            FilledTonalButton(
                onClick = onSend,
                enabled = enabled && sendEnabled && valid && value.isNotBlank() && !sending,
                modifier = Modifier.height(52.dp),
                contentPadding = PaddingValues(horizontal = 16.dp),
                shape = RoundedCornerShape(24.dp),
                colors = ButtonDefaults.filledTonalButtonColors(containerColor = MaterialTheme.colorScheme.primary, contentColor = MaterialTheme.colorScheme.onPrimary)
            ) { Text(if (sending) "…" else if (queue) "Queue" else "Send") }
        }
        if (inputNotice != null || !valid) Text(inputNotice ?: "Shorten this message to 6,000 bytes.", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(bottom = 6.dp))
        else if (bytes >= 5400) Text("$bytes / 6,000 bytes", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.align(Alignment.End).padding(bottom = 6.dp))
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun ChatMessageBubble(
    chat: Chat,
    modifier: Modifier = Modifier,
    showDetails: Boolean = false,
    onLongClick: () -> Unit = {},
    content: @Composable ColumnScope.() -> Unit = { ChatText(chat.text) }
) {
    val scheme = MaterialTheme.colorScheme
    val background = if (chat.outgoing) scheme.primaryContainer else scheme.surfaceContainerHigh
    val foreground = if (chat.outgoing) scheme.onPrimaryContainer else scheme.onSurface
    Row(modifier.fillMaxWidth(), horizontalArrangement = if (chat.outgoing) Arrangement.End else Arrangement.Start) {
        Card(
            Modifier.padding(start = if (chat.outgoing) 28.dp else 0.dp, end = if (chat.outgoing) 0.dp else 28.dp)
                .widthIn(min = 76.dp, max = 360.dp)
                .combinedClickable(onClick = {}, onLongClick = onLongClick),
            colors = CardDefaults.cardColors(containerColor = background, contentColor = foreground),
            shape = RoundedCornerShape(topStart = 20.dp, topEnd = 20.dp, bottomStart = if (chat.outgoing) 20.dp else 6.dp, bottomEnd = if (chat.outgoing) 6.dp else 20.dp),
            border = BorderStroke(1.dp, scheme.outlineVariant.copy(alpha = .45f))
        ) {
            Column(Modifier.padding(horizontal = 14.dp, vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                content()
                val delivery = when {
                    !chat.outgoing -> ""
                    showDetails -> chat.status
                    chat.status == "Delivered" -> "✓"
                    chat.status == "Sending" -> "Sending"
                    chat.status.startsWith("Failed") -> "Failed"
                    chat.status.startsWith("No acknowledgment") -> "Unconfirmed"
                    chat.status.startsWith("Interrupted") -> "Interrupted"
                    else -> chat.status
                }
                val time = DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(chat.time))
                val plain = chat.security.startsWith("Plaintext", ignoreCase = true)
                val security = if (plain) "Unencrypted" else if (showDetails) chat.security else ""
                Text(
                    listOf(time, delivery, security).filter { it.isNotBlank() }.joinToString(" · "),
                    modifier = Modifier.align(Alignment.End),
                    style = MaterialTheme.typography.labelSmall,
                    color = if (plain) scheme.error else foreground.copy(alpha = .65f)
                )
            }
        }
    }
}

@Composable
fun AttachmentAction(title: String, note: String, symbol: String, enabled: Boolean = true, onClick: () -> Unit) {
    TextButton(onClick = onClick, enabled = enabled, modifier = Modifier.fillMaxWidth(), contentPadding = PaddingValues(horizontal = 20.dp, vertical = 12.dp)) {
        Surface(shape = RoundedCornerShape(14.dp), color = MaterialTheme.colorScheme.secondaryContainer.copy(alpha = if (enabled) 1f else .4f), modifier = Modifier.size(44.dp)) {
            Box(contentAlignment = Alignment.Center) { Text(symbol, style = MaterialTheme.typography.titleLarge) }
        }
        Spacer(Modifier.width(14.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall, color = if (enabled) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurface.copy(alpha = .38f))
            Text(note, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = if (enabled) 1f else .5f))
        }
    }
}
