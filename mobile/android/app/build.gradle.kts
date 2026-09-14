plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }
android {
    namespace = "cc.flytown.memberprogress"
    compileSdk = 36
    buildToolsVersion = "36.0.0"
    defaultConfig { applicationId = "cc.flytown.memberprogress"; minSdk = 29; targetSdk = 36; versionCode = 7; versionName = "1.0.7" }
    val storePath = System.getenv("MEMBER_ANDROID_KEYSTORE")
    if (!storePath.isNullOrBlank()) {
        signingConfigs.create("privateRelease") {
            storeFile = file(storePath)
            storePassword = System.getenv("MEMBER_ANDROID_STORE_PASSWORD")
            keyAlias = "member-progress"
            keyPassword = System.getenv("MEMBER_ANDROID_KEY_PASSWORD")
        }
    }
    buildTypes { getByName("release") { isMinifyEnabled = false; if (!storePath.isNullOrBlank()) signingConfig = signingConfigs.getByName("privateRelease") } }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    lint { abortOnError = true }
}
dependencies { implementation("androidx.work:work-runtime-ktx:2.10.1") }
