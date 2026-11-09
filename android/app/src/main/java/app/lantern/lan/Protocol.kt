package app.lantern.lan

import java.nio.charset.Charset

object Wire {
    const val PORT = 2425
    const val FILE = 0x00200000L
    const val ENCRYPT = 0x00400000L
    const val UTF8 = 0x00800000L
    const val CAP_UTF8 = 0x01000000L
    const val EXT = 0x04000000L
    const val FILE_ENC = 0x00040000L
    const val CAPS = FILE or ENCRYPT or UTF8 or CAP_UTF8 or EXT or FILE_ENC
    data class Packet(val number: Long, val user: String, val host: String, val command: Long, val body: String) {
        val mode get() = (command and 255).toInt()
        fun bytes(): ByteArray {
            require(number in 0..0xffffffffL)
            require(user.none { it == ':' || it == '\u0000' } && host.none { it == ':' || it == '\u0000' })
            val charset = if (command and UTF8 != 0L) Charsets.UTF_8 else Charset.forName("windows-31j")
            return ("1:$number:$user:$host:$command:$body\u0000").toByteArray(charset).also { require(it.size <= 32768) { "Message too large" } }
        }
    }
    fun parse(bytes: ByteArray): Packet {
        require(bytes.size <= 32768)
        val ascii = bytes.toString(Charsets.ISO_8859_1).split(':', limit = 6)
        require(ascii.size == 6 && ascii[0] == "1") { "Unsupported packet" }
        val cmd = ascii[4].toLong().also { require(it in 0..0xffffffffL) }
        val cs = if (cmd and UTF8 != 0L) Charsets.UTF_8 else Charset.forName("windows-31j")
        val fields = bytes.toString(cs).split(':', limit = 6)
        return Packet(fields[1].toLong().also { require(it in 0..0xffffffffL) }, fields[2], fields[3], cmd, fields[5].removeSuffix("\u0000"))
    }
    data class Attachment(val id: Long, val name: String, val size: Long, val attributes: Long = 1) {
        fun offer() = "$id:${name.replace(":", "::")}:${size.toString(16)}:0:${attributes.toString(16)}:\u0007"
    }
    fun attachments(text: String): List<Attachment> = text.split('\u0007').mapNotNull { record ->
        runCatching {
            // Doubled colons are legal only in the filename field, including a trailing colon.
            val s = record.removePrefix(":")
            val first = s.indexOf(':'); require(first > 0)
            var i = first + 1
            val name = StringBuilder()
            while (i < s.length) {
                if (s[i] == ':') {
                    if (i + 1 < s.length && s[i + 1] == ':') { name.append(':'); i += 2; continue }
                    i++; break
                }
                name.append(s[i++])
            }
            val rest = s.substring(i).split(':')
            val size = rest[0].toLong(16); require(size >= 0)
            Attachment(s.substring(0, first).toLong(), name.toString().take(240), size, rest[2].toLong(16))
        }.getOrNull()
    }.filter { it.attributes and 255 == 1L } // No directories, symlinks or embedded-image pseudo-files.
    fun safeName(name: String) = name.substringAfterLast('/').substringAfterLast('\\')
        .replace(Regex("[\\p{Cntrl}:*?\"<>|]"), "_").trim().take(180).let { if (it.isBlank() || it == "." || it == "..") "received-file" else it }
    fun fileHeader(file: Attachment): ByteArray {
        val tail = ":${safeName(file.name)}:${file.size.toString(16)}:1:"
        val length = 4 + tail.toByteArray(Charsets.UTF_8).size
        require(length <= 65535)
        return ("%04x".format(length) + tail).toByteArray(Charsets.UTF_8)
    }
}

/** Bounded replay memory; acknowledge duplicates but never display them twice. */
class ReplayWindow(private val capacity: Int = 2048) {
    private val seen = LinkedHashSet<String>()
    @Synchronized fun first(key: String): Boolean {
        if (!seen.add(key)) return false
        if (seen.size > capacity) seen.remove(seen.first())
        return true
    }
}
