allprojects {
    repositories {
        google()
        mavenCentral()
    }
}

// 将 Android 产物归入当前 Flutter 应用的 build 目录，各子项目继续使用独立子目录。
// 这里只调整本项目输出，不复用其他分支的 Gradle/CMake 产物。
val newBuildDir: Directory =
    rootProject.layout.buildDirectory
        .dir("../../build")
        .get()
rootProject.layout.buildDirectory.value(newBuildDir)

subprojects {
    val newSubprojectBuildDir: Directory = newBuildDir.dir(project.name)
    project.layout.buildDirectory.value(newSubprojectBuildDir)
}
subprojects {
    project.evaluationDependsOn(":app")
}

tasks.register<Delete>("clean") {
    delete(rootProject.layout.buildDirectory)
}
