package app.lantern.lan

import org.junit.Assert.*
import org.junit.Test

class PacketRateLimitTest {
    @Test fun burstBudgetResetsAndDoesNotAffectOtherPeers() {
        val limit = PacketRateLimit(3, 100)
        repeat(3) { assertTrue(limit.allow("one", 1000)) }
        assertFalse(limit.allow("one", 1001))
        assertTrue(limit.allow("two", 1001))
        assertTrue(limit.allow("one", 1100))
    }
    @Test fun manyAddressesStayUsableWithBoundedTracking() {
        val limit = PacketRateLimit(1, 100)
        repeat(1000) { assertTrue(limit.allow("peer-$it", 1000)) }
        assertFalse(limit.allow("peer-999", 1001))
        assertTrue(limit.allow("peer-0", 1001))
    }
}
