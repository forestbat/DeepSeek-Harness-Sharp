import java.awt.GraphicsEnvironment;
import java.awt.Rectangle;
import java.awt.Robot;
import java.awt.Toolkit;
import java.awt.datatransfer.StringSelection;
import java.awt.event.InputEvent;
import java.awt.event.KeyEvent;
import java.awt.image.BufferedImage;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Base64;
import java.util.HashSet;
import javax.imageio.ImageIO;

/** Input and pixel capture only. Targets and readiness come from the real application's visual tree. */
public final class NetworkGuiDriver {
    private enum Key {
        HOME(KeyEvent.VK_HOME), ENTER(KeyEvent.VK_ENTER), DOWN(KeyEvent.VK_DOWN),
        ALT(KeyEvent.VK_ALT), F4(KeyEvent.VK_F4);

        final int code;
        Key(int code) { this.code = code; }
    }

    public static void main(String[] args) throws Exception {
        String display = System.getenv("DISPLAY");
        if (args.length != 2 || display == null || !display.equals(args[0]) || !display.matches(":[0-9]+"))
            throw new IllegalStateException("A dedicated Xvfb display is required.");
        Path owned = Path.of(args[1]).toRealPath();
        Robot robot = new Robot(GraphicsEnvironment.getLocalGraphicsEnvironment().getDefaultScreenDevice());
        robot.setAutoWaitForIdle(true);
        System.out.println("READY");
        System.out.flush();
        try (BufferedReader input = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8))) {
            for (String line; (line = input.readLine()) != null;) {
                String[] command = line.split("\\t", -1);
                switch (command[0]) {
                    case "CLICK" -> {
                        robot.mouseMove(Integer.parseInt(command[1]), Integer.parseInt(command[2]));
                        robot.mousePress(InputEvent.BUTTON1_DOWN_MASK);
                        robot.mouseRelease(InputEvent.BUTTON1_DOWN_MASK);
                    }
                    case "DRAG" -> {
                        int x = Integer.parseInt(command[1]);
                        int y = Integer.parseInt(command[2]);
                        robot.mouseMove(x, y);
                        robot.mousePress(InputEvent.BUTTON1_DOWN_MASK);
                        robot.mouseMove(x, y + 5);
                        robot.mouseMove(x, Integer.parseInt(command[3]));
                        robot.mouseRelease(InputEvent.BUTTON1_DOWN_MASK);
                    }
                    case "TEXT" -> {
                        Toolkit.getDefaultToolkit().getSystemClipboard().setContents(new StringSelection(
                            new String(Base64.getDecoder().decode(command[1]), StandardCharsets.UTF_8)), null);
                        chord(robot, KeyEvent.VK_CONTROL, KeyEvent.VK_A);
                        chord(robot, KeyEvent.VK_CONTROL, KeyEvent.VK_V);
                    }
                    case "PASTE_TERMINAL" -> {
                        Toolkit.getDefaultToolkit().getSystemClipboard().setContents(new StringSelection(
                            new String(Base64.getDecoder().decode(command[1]), StandardCharsets.UTF_8)), null);
                        robot.keyPress(KeyEvent.VK_CONTROL);
                        robot.keyPress(KeyEvent.VK_SHIFT);
                        robot.keyPress(KeyEvent.VK_V);
                        robot.keyRelease(KeyEvent.VK_V);
                        robot.keyRelease(KeyEvent.VK_SHIFT);
                        robot.keyRelease(KeyEvent.VK_CONTROL);
                    }
                    case "KEY" -> {
                        for (int index = 1; index < command.length; index++) robot.keyPress(Key.valueOf(command[index]).code);
                        for (int index = command.length - 1; index > 0; index--) robot.keyRelease(Key.valueOf(command[index]).code);
                    }
                    case "SIZE" -> {
                        Process resize = new ProcessBuilder("xdotool", "windowsize", command[1], command[2], command[3]).start();
                        if (resize.waitFor() != 0) throw new IllegalStateException("Owned X11 window resize failed.");
                        Process move = new ProcessBuilder("xdotool", "windowmove", command[1], "0", "0").start();
                        if (move.waitFor() != 0) throw new IllegalStateException("Owned X11 window move failed.");
                    }
                    case "SHOT" -> {
                        Path path = owned.resolve(command[1]).normalize();
                        if (!path.startsWith(owned) || !path.getParent().equals(owned))
                            throw new IllegalArgumentException("Screenshots must stay in the owned evidence directory.");
                        Files.createDirectories(path.getParent());
                        robot.waitForIdle();
                        BufferedImage image = robot.createScreenCapture(new Rectangle(Integer.parseInt(command[2]),
                            Integer.parseInt(command[3]), Integer.parseInt(command[4]), Integer.parseInt(command[5])));
                        ImageIO.write(image, "png", path.toFile());
                        HashSet<Integer> colors = new HashSet<>();
                        for (int y = 0; y < image.getHeight(); y += 4)
                            for (int x = 0; x < image.getWidth(); x += 4) colors.add(image.getRGB(x, y));
                        if (colors.size() < 16)
                            throw new IllegalStateException("The mapped window has not produced a nonblank rendered frame.");
                    }
                    case "EXIT" -> { System.out.println("OK"); System.out.flush(); return; }
                    default -> throw new IllegalArgumentException("Unknown input operation.");
                }
                robot.waitForIdle();
                Toolkit.getDefaultToolkit().sync();
                System.out.println("OK");
                System.out.flush();
            }
        }
    }

    private static void chord(Robot robot, int modifier, int key) {
        robot.keyPress(modifier);
        robot.keyPress(key);
        robot.keyRelease(key);
        robot.keyRelease(modifier);
    }
}
