# Spec Delta

## MODIFIED Requirements

### Requirement: Tell the child about a suspension
When the system suspends a program because its exe has no time left, it SHALL send one session message per limit and day: one for each Category Limit and one for each App Limit. The message SHALL name the limit that suspends the program: the app for an App Limit, as the App Limit warning does, and the category for a Category Limit. The message SHALL say that the programs are paused, not closed, and continue tomorrow or after Bonus Minutes. One text serves both limits: its placeholder `{name}` is the app or the category, and `{category}` is always the category. The parent SHALL be able to change the text.

#### Scenario: Message on suspension
- **WHEN** the system suspends the Games programs for the first time today because of the Games Category Limit
- **THEN** the child gets one message that names Games and says the programs are paused

#### Scenario: App Limit names the app
- **WHEN** Games has the weekday limit 180, the `TestGame.exe` rule has the App Limit 120, and the Games usage reaches 120 minutes on a Tuesday while TestGame runs
- **THEN** TestGame is suspended and the child gets one message that names TestGame and says it is paused
- **AND** the message does not name Games as the program that stops

#### Scenario: Second process of the same App Limit
- **WHEN** the child got the message for TestGame today and then starts TestGame again, and the system suspends it
- **THEN** the child gets no second message for TestGame today

#### Scenario: App Limit and Category Limit on one day
- **WHEN** the child got the message for TestGame at 120 minutes, and the Games usage then reaches the Games limit of 180 minutes on the same day while another game runs
- **THEN** the system suspends the other game and the child gets one message that names Games
