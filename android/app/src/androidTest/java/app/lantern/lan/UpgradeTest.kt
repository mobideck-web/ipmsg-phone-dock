package app.lantern.lan

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class UpgradeTest {
    @Test fun existingIdentityAndReceivedHistorySurviveReleaseKeyRotation() {
        val expected=InstrumentationRegistry.getArguments().getString("expectedUser")
        assumeTrue(expected != null)
        val model=Model.get(InstrumentationRegistry.getInstrumentation().targetContext)
        assertEquals(expected,model.user)
        assertEquals(1,model.repository.search("upgrade-history-preserved-v06").size)
        assertEquals(6,model.history.readableDatabase.version)
        assertEquals(0,model.context.applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE)
    }
}
